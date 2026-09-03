using AtualizadorERP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AtualizadorERP.Tests;

/// <summary>
/// Cobre, com Firebird e isql reais, os achados do item 1 do RISCOS-CONHECIDOS.md: scripts
/// isolados por processo, scripts já aplicados antes de existir a tabela SCRIPTS, nomes
/// duplicados entre subpastas e um script quebrado não travando o lote.
/// </summary>
public class ScriptRunnerServiceTests
{
    private readonly ScriptRunnerService _scriptRunnerService;

    public ScriptRunnerServiceTests()
    {
        var databaseService = new DatabaseService(TestAmbiente.Config);
        var processService = new ProcessService(NullLogger<ProcessService>.Instance);
        var apiService = new ApiService(TestAmbiente.Config);
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
    public async Task Scripts_com_mesmo_nome_em_subpastas_diferentes_aplicam_os_dois()
    {
        // Achado real: 26 nomes de arquivo duplicados entre subpastas de Scripts-BVendas. Registrar
        // só pelo nome faria o segundo ser ignorado como "já aplicado" pra sempre.
        using var junior = FirebirdTestDatabase.CriarJunior();
        var pasta = NovaPastaPacotes();
        try
        {
            Directory.CreateDirectory(Path.Combine(pasta, "scripts2012"));
            Directory.CreateDirectory(Path.Combine(pasta, "scripts2016"));
            File.WriteAllText(Path.Combine(pasta, "scripts2012", "Cria_campo_x.sql"), "CREATE TABLE TABELA_2012 (ID INTEGER);");
            File.WriteAllText(Path.Combine(pasta, "scripts2016", "Cria_campo_x.sql"), "CREATE TABLE TABELA_2016 (ID INTEGER);");

            int falhas = await _scriptRunnerService.RunPendingScriptsAsync(junior.CaminhoArquivo, pasta, "00000000000000", "SISTEMA_TESTE");

            Assert.Equal(0, falhas);
            var aplicados = new DatabaseService(TestAmbiente.Config).GetScriptsAplicados(junior.CaminhoArquivo);
            Assert.Contains(Path.Combine("scripts2012", "Cria_campo_x.sql"), aplicados);
            Assert.Contains(Path.Combine("scripts2016", "Cria_campo_x.sql"), aplicados);
            Assert.True(new DatabaseService(TestAmbiente.Config).VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE TABELA_2012 (ID INTEGER)").JaExiste);
            Assert.True(new DatabaseService(TestAmbiente.Config).VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE TABELA_2016 (ID INTEGER)").JaExiste);
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }
}
