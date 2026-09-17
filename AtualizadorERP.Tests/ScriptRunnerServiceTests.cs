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
