using AtualizadorERP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AtualizadorERP.Tests;

/// <summary>
/// Cobre, com Firebird e isql reais, os achados do item 1 do RISCOS-CONHECIDOS.md: scripts
/// isolados por processo, scripts já aplicados antes de existir a tabela SCRIPTS, subpastas
/// (arquivo histórico) ignoradas na execução e um script quebrado não travando o lote.
/// </summary>
public class ScriptRunnerServiceTests
{
    private readonly ScriptRunnerService _scriptRunnerService;

    public ScriptRunnerServiceTests()
    {
        var databaseService = new DatabaseService(TestAmbiente.Config);
        var processService = new ProcessService(NullLogger<ProcessService>.Instance);
        var apiService = new ApiService(NullLogger<ApiService>.Instance, TestAmbiente.Config);
        _scriptRunnerService = new ScriptRunnerService(NullLogger<ScriptRunnerService>.Instance, databaseService, processService, apiService, TestAmbiente.Config);
    }

    private static string NovaPastaPacotes()
    {
        return Directory.CreateTempSubdirectory().FullName;
    }

    [Fact]
    public async Task Script_que_depende_de_outro_mais_adiante_na_ordem_alfabetica_aplica_na_segunda_passada()
    {
        // Pedido real: alguns scripts dependem de um objeto que só é criado por outro script mais
        // adiante na MESMA leva -- ordem alfabética do nome do arquivo (o critério de execução)
        // nem sempre bate com ordem de dependência real. "A_..." roda antes de "B_...", mas
        // A_Cria_tabela_filha.sql referencia (via FOREIGN KEY) uma tabela que só
        // B_Cria_tabela_pai.sql cria -- falha na 1ª passada, mas B já aplicou nela, então a 2ª
        // passada (que RunPendingScriptsAsync roda sozinho) pega o A de novo e aplica limpo.
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            File.WriteAllText(
                Path.Combine(pasta, "A_Cria_tabela_filha.sql"),
                "CREATE TABLE TABELA_FILHA_TESTE (ID INTEGER, ID_PAI INTEGER, FOREIGN KEY (ID_PAI) REFERENCES TABELA_PAI_TESTE(ID));");
            File.WriteAllText(
                Path.Combine(pasta, "B_Cria_tabela_pai.sql"),
                "CREATE TABLE TABELA_PAI_TESTE (ID INTEGER PRIMARY KEY);");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            var aplicados = new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo);
            Assert.Contains("A_Cria_tabela_filha.sql", aplicados);
            Assert.Contains("B_Cria_tabela_pai.sql", aplicados);
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Aplica_script_novo_e_registra_em_SCRIPTS()
    {
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            File.WriteAllText(Path.Combine(pasta, "Cria_tabela_x.sql"), "CREATE TABLE TABELA_X (ID INTEGER);");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            Assert.Contains("Cria_tabela_x.sql", new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo));
            var verificacao = new DatabaseService(TestAmbiente.Config).VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE TABELA_X (ID INTEGER);");
            Assert.True(verificacao.JaExiste);
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Nao_reaplica_script_ja_registrado_em_SCRIPTS()
    {
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            File.WriteAllText(Path.Combine(pasta, "Cria_tabela_y.sql"), "CREATE TABLE TABELA_Y (ID INTEGER);");

            await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");
            // Segunda rodada, mesmo pacote: não deve tentar recriar a tabela (o que falharia com
            // "already exists") -- é o próprio propósito de reaproveitar a tabela SCRIPTS.
            int falhasSegundaRodada = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhasSegundaRodada);
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Script_cujo_objeto_ja_existe_e_marcado_aplicado_sem_executar()
    {
        // Simula o achado real do item 1: um domain/tabela criado décadas atrás, nunca registrado
        // em SCRIPTS porque esse controle não existia ainda. Rodar o CREATE de novo quebraria com
        // "already exists" -- a verificação prévia evita isso marcando como aplicado direto.
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            junior.ExecutarNaoConsulta("CREATE TABLE TABELA_LEGADA (ID INTEGER);");
            File.WriteAllText(Path.Combine(pasta, "Cria_tabela_legada.sql"), "CREATE TABLE TABELA_LEGADA (ID INTEGER);");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            Assert.Contains("Cria_tabela_legada.sql", new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo));
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Script_sem_ponto_e_virgula_final_e_aplicado_mesmo_assim()
    {
        // Achado real (cliente Bredas): scripts gerados pra rodar no BScript.exe/IBExpert nem
        // sempre terminam com ";" -- os dois executam o texto inteiro como um comando só, sem
        // exigir terminador. O isql (usado aqui) exige, e sem ele falha com "unexpected end of
        // command" mesmo a sintaxe estando perfeita -- confirmado contra Firebird real. O agente
        // precisa aplicar esses scripts do mesmo jeito que o BScript/IBExpert já aplicavam.
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            junior.ExecutarNaoConsulta("CREATE TABLE CONF_EMP_COMPLEMENTO3 (ID INTEGER);");
            File.WriteAllText(
                Path.Combine(pasta, "Cria_campo_sem_pv.sql"),
                "ALTER TABLE CONF_EMP_COMPLEMENTO3\nADD USA_BANDEIRA_DEFAULT VARCHAR(6)\nDEFAULT 'False'");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            Assert.Contains("Cria_campo_sem_pv.sql", new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo));
            Assert.True(new DatabaseService(TestAmbiente.Config)
                .VerificarObjetoDdl(junior.CaminhoArquivo, "ALTER TABLE CONF_EMP_COMPLEMENTO3 ADD USA_BANDEIRA_DEFAULT VARCHAR(6)")
                .JaExiste);
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Script_de_trigger_com_corpo_sem_set_term_e_aplicado_envolvendo_automaticamente()
    {
        // Achado real (1027 scripts reais do B_Vendas testados): CREATE/ALTER TRIGGER/PROCEDURE
        // com corpo BEGIN...END sem o próprio "SET TERM" falha no isql -- o ";" de dentro do
        // corpo quebra o comando no meio (o terminador padrão do isql é ";"), gerando uma cascata
        // de erros de sintaxe. O agente precisa envolver automaticamente com "SET TERM ^ ;" /
        // "SET TERM ; ^", já que os scripts legados nunca trazem isso (não precisavam rodar em
        // isql antes).
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            junior.ExecutarNaoConsulta("CREATE TABLE TABELA_TRIGGER_TESTE (ID INTEGER, VALOR INTEGER);");
            File.WriteAllText(
                Path.Combine(pasta, "Cria_trigger_sem_set_term.sql"),
                "CREATE TRIGGER TRG_TESTE_BIU0 FOR TABELA_TRIGGER_TESTE\n" +
                "ACTIVE BEFORE INSERT OR UPDATE POSITION 0\n" +
                "AS\n" +
                "BEGIN\n" +
                "  IF (NEW.VALOR IS NULL) THEN\n" +
                "    NEW.VALOR = 0;\n" +
                "  IF (NEW.ID IS NULL) THEN\n" +
                "    NEW.ID = 1;\n" +
                "END");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            Assert.Contains("Cria_trigger_sem_set_term.sql", new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo));
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Script_com_DROP_e_SET_antes_do_corpo_envolve_so_a_partir_do_comando_com_corpo()
    {
        // Achado real (1027 scripts reais do B_Vendas): "20260121Altera_Trigger_PRE_PEDIDO_AU0_ANSI.sql"
        // tem "DROP TRIGGER X;\nSET SQL DIALECT 3;\nSET NAMES ISO8859_1;\n\nCREATE OR ALTER TRIGGER
        // X ... AS BEGIN...END" -- uma primeira versão da correção do SET TERM envolvia o ARQUIVO
        // INTEIRO, engolindo o ";" desses comandos anteriores (viravam texto dentro do "^") e
        // quebrando o parser logo no "SET SQL DIALECT" ("Token unknown ... SET"). Só o
        // CREATE/ALTER TRIGGER (com corpo) deve ser envolvido, o que vem antes fica sob ";".
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            junior.ExecutarNaoConsulta("CREATE TABLE TABELA_DROP_ANTES_TESTE (ID INTEGER, VALOR INTEGER);");
            junior.ExecutarNaoConsulta(
                "CREATE TRIGGER TRG_DROP_ANTES_TESTE FOR TABELA_DROP_ANTES_TESTE ACTIVE BEFORE INSERT POSITION 0 AS BEGIN END");
            File.WriteAllText(
                Path.Combine(pasta, "Altera_trigger_com_drop_antes.sql"),
                "DROP TRIGGER TRG_DROP_ANTES_TESTE;\n" +
                "SET SQL DIALECT 3;\n" +
                "SET NAMES ISO8859_1;\n" +
                "\n" +
                "CREATE OR ALTER TRIGGER TRG_DROP_ANTES_TESTE FOR TABELA_DROP_ANTES_TESTE\n" +
                "ACTIVE BEFORE INSERT POSITION 0\n" +
                "AS\n" +
                "BEGIN\n" +
                "  IF (NEW.VALOR IS NULL) THEN\n" +
                "    NEW.VALOR = 0;\n" +
                "  IF (NEW.ID IS NULL) THEN\n" +
                "    NEW.ID = 1;\n" +
                "END");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            Assert.Contains("Altera_trigger_com_drop_antes.sql", new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo));
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Script_em_ScriptsIgnorados_nunca_roda_e_nao_reporta_erro()
    {
        // Achado real: 20241009Altera_Procedure_Inventario_NFCe.sql, um script legado do B_Vendas
        // que é só o corpo solto de uma procedure, sem o cabeçalho "ALTER PROCEDURE ... AS" --
        // nenhuma correção automática resolve porque o próprio arquivo está incompleto. Simula com
        // um SQL igualmente quebrado (sintaxe inválida de propósito) pra provar que ele nunca
        // chega a rodar no isql quando está em SCRIPTS_IGNORADOS.
        var configComIgnorado = TestAmbiente.NovaConfiguracao(scriptsIgnorados: new[] { "Script_quebrado_ignorado.sql" });
        var databaseService = new DatabaseService(configComIgnorado);
        var processService = new ProcessService(NullLogger<ProcessService>.Instance);
        var apiService = new ApiService(NullLogger<ApiService>.Instance, configComIgnorado);
        var scriptRunnerService = new ScriptRunnerService(NullLogger<ScriptRunnerService>.Instance, databaseService, processService, apiService, configComIgnorado);

        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            File.WriteAllText(Path.Combine(pasta, "Script_quebrado_ignorado.sql"), "BEGIN\n  x := 1;\nEND");

            int falhas = await scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            Assert.DoesNotContain("Script_quebrado_ignorado.sql", databaseService.GetScriptsAplicados(junior.CaminhoArquivo));
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Script_de_tipo_nao_reconhecido_cujo_objeto_ja_existe_e_marcado_aplicado_sem_reportar_erro()
    {
        // Mesmo cenário legado do teste acima (objeto criado décadas atrás, nunca registrado em
        // SCRIPTS), mas para um tipo de DDL fora da lista de VerificarObjetoDdl (CREATE EXCEPTION)
        // -- o pré-check não pega, então é o isql quem roda de verdade e retorna "already exists".
        // Isso não pode virar falha reportada à API: é sincronizar o controle com a realidade do
        // banco, igual ao caso reconhecido.
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            junior.ExecutarNaoConsulta("CREATE EXCEPTION EXC_LEGADA 'mensagem legada';");
            File.WriteAllText(Path.Combine(pasta, "Cria_exception_legada.sql"), "CREATE EXCEPTION EXC_LEGADA 'mensagem legada';");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            Assert.Contains("Cria_exception_legada.sql", new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo));
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Script_quebrado_nao_trava_o_lote_e_e_reportado_como_falha()
    {
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            File.WriteAllText(Path.Combine(pasta, "01_quebrado.sql"), "CREATE TABLE (SINTAXE INVALIDA);");
            File.WriteAllText(Path.Combine(pasta, "02_valido.sql"), "CREATE TABLE TABELA_VALIDA (ID INTEGER);");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(1, falhas);
            var aplicados = new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo);
            Assert.DoesNotContain("01_quebrado.sql", aplicados);
            Assert.Contains("02_valido.sql", aplicados);
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public async Task Scripts_em_subpastas_profundas_sao_ignorados_raiz_e_um_nivel_abaixo_rodam()
    {
        // Achado real (cliente Bredas, conferido com "7za l" no pacote publicado de verdade): o
        // pacote NUNCA solta .sql direto nele -- embrulha tudo numa pasta por sistema
        // ("Scripts-BVendas\", ao lado de "Dlls-BVendas\"), espelhando a estrutura da pasta do
        // cliente. Dentro dela, subpastas tipo "scripts2012\"/"scripts2016\" são arquivo histórico
        // do BScript.exe, não pendência nova -- rodar por engano os de lá polui o relatório com
        // falhas que nunca foram de verdade (25 "erro" reportados num teste real, todos scripts de
        // anos atrás já aplicados manualmente). Uma correção anterior tentou restringir a busca a
        // "SearchOption.TopDirectoryOnly" direto em pacotesPath -- e não achava NENHUM script,
        // porque a raiz de verdade é "Scripts-BVendas\", um nível abaixo, não pacotesPath em si.
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            Directory.CreateDirectory(Path.Combine(pasta, "Scripts-BVendas", "scripts2012"));
            File.WriteAllText(Path.Combine(pasta, "Scripts-BVendas", "scripts2012", "Cria_campo_x.sql"), "CREATE TABLE TABELA_2012 (ID INTEGER);");
            File.WriteAllText(Path.Combine(pasta, "Scripts-BVendas", "Cria_tabela_um_nivel.sql"), "CREATE TABLE TABELA_UM_NIVEL (ID INTEGER);");
            File.WriteAllText(Path.Combine(pasta, "Cria_tabela_raiz.sql"), "CREATE TABLE TABELA_RAIZ (ID INTEGER);");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            var aplicados = new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo);
            Assert.Contains("Cria_tabela_raiz.sql", aplicados);
            Assert.Contains("Cria_tabela_um_nivel.sql", aplicados);
            Assert.DoesNotContain("Cria_campo_x.sql", aplicados);
            Assert.False(new DatabaseService(TestAmbiente.Config).VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE TABELA_2012 (ID INTEGER)").JaExiste);
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }
}
