using System.Security.Cryptography;
using AtualizadorERP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AtualizadorERP.Tests;

/// <summary>
/// Automatiza o teste manual de ponta a ponta (Fase 3 -> Fase 4) registrado em
/// RISCOS-CONHECIDOS.md -- "vale repetir antes de qualquer mudança futura no
/// Worker.cs/DatabaseService.cs". Chama Worker.ProcessarAtualizacao diretamente (internal, ver
/// AssemblyInfo.cs) contra bancos Firebird descartáveis, criados do zero -- não cópias de
/// produção. Fase 2 (autorização pelo ERP Delphi) não existe ainda, então o teste simula o mesmo
/// jeito que o teste manual simulou: grava STATUS=AUTORIZADO direto no banco antes de chamar.
/// </summary>
public class WorkerIntegrationTests
{
    public WorkerIntegrationTests() => TestAmbiente.Garantir();

    private static (string TempPath, string PastaPacotes) NovoTempPath()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"atualizador_worker_teste_{Guid.NewGuid():N}");
        string pastaPacotes = Path.Combine(tempPath, "pacotes");
        Directory.CreateDirectory(pastaPacotes);
        return (tempPath, pastaPacotes);
    }

    private static Worker NovoWorker(string juniorPath, string bexePath, string tempPath, out DatabaseService databaseService)
    {
        Environment.SetEnvironmentVariable("ATUALIZADOR_JUNIOR_FDB", juniorPath);
        Environment.SetEnvironmentVariable("ATUALIZADOR_BEXE_FDB", bexePath);
        Environment.SetEnvironmentVariable("ATUALIZADOR_TEMP_PATH", tempPath);

        databaseService = new DatabaseService();
        var processService = new ProcessService(NullLogger<ProcessService>.Instance);
        var apiService = new ApiService();
        var extractionService = new ExtractionService(NullLogger<ExtractionService>.Instance, processService);
        var scriptRunnerService = new ScriptRunnerService(NullLogger<ScriptRunnerService>.Instance, databaseService, processService, apiService);

        return new Worker(NullLogger<Worker>.Instance, apiService, databaseService, extractionService, processService, scriptRunnerService);
    }

    [Fact]
    public async Task Ciclo_completo_com_sucesso_promove_versao_e_injeta_binario()
    {
        using var junior = FirebirdTestDatabase.CriarJunior(status: "AUTORIZADO", versaoAtual: "1.0.0", versaoNova: "9.9.9");
        using var bexe = FirebirdTestDatabase.CriarBexe();
        var (tempPath, pastaPacotes) = NovoTempPath();
        try
        {
            byte[] conteudoExe = { 1, 2, 3, 4, 5, 6, 7 };
            File.WriteAllBytes(Path.Combine(pastaPacotes, "produto_teste.exe"), conteudoExe);
            File.WriteAllText(Path.Combine(pastaPacotes, "Cria_tabela_teste.sql"), "CREATE TABLE TABELA_CICLO_COMPLETO (ID INTEGER);");

            var worker = NovoWorker(junior.CaminhoArquivo, bexe.CaminhoArquivo, tempPath, out var databaseService);

            await worker.ProcessarAtualizacao(CancellationToken.None);

            Assert.Equal("CONCLUIDO", databaseService.GetStatusAtualizacao(junior.CaminhoArquivo));
            Assert.Equal("9.9.9", databaseService.GetVersaoConfirmada(junior.CaminhoArquivo));
            Assert.Contains("Cria_tabela_teste.sql", databaseService.GetScriptsAplicados(junior.CaminhoArquivo));
            Assert.True(databaseService.VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE TABELA_CICLO_COMPLETO (ID INTEGER)").JaExiste);

            string hashEsperado = Convert.ToHexString(SHA256.HashData(conteudoExe)).ToLowerInvariant();
            Assert.Equal(hashEsperado, bexe.ExecutarEscalar("SELECT HASHEXE FROM EXECUTAVEIS WHERE NOMEARQUIVO = 'produto_teste.exe'"));
            Assert.Equal("9.9.9", bexe.ExecutarEscalar("SELECT VERSAOATUALIZADA FROM EXECUTAVEIS WHERE NOMEARQUIVO = 'produto_teste.exe'"));

            // Caminho de sucesso apaga o TEMP_PATH inteiro -- nada de backup ou pacote sobra pra
            // trás pra próxima tentativa confundir com (ver itens 2 e 3 do RISCOS-CONHECIDOS.md).
            Assert.False(Directory.Exists(tempPath));
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task Falha_na_fase_4_reverte_o_banco_via_backup_e_nao_promove_versao()
    {
        // "2026.08.27" tem 10 caracteres -- estoura o limite real de 5 do VERSAOATUALIZADA (item
        // 14) só depois que o gfix -shut, o gbak pré e os scripts já rodaram. É o cenário exato pra
        // testar o rollback: precisa restaurar um banco que já tinha mudado de estado.
        using var junior = FirebirdTestDatabase.CriarJunior(status: "AUTORIZADO", versaoAtual: "1.0.0", versaoNova: "2026.08.27");
        using var bexe = FirebirdTestDatabase.CriarBexe();
        var (tempPath, pastaPacotes) = NovoTempPath();
        try
        {
            File.WriteAllBytes(Path.Combine(pastaPacotes, "produto_teste.exe"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(pastaPacotes, "Cria_tabela_sera_revertida.sql"), "CREATE TABLE TABELA_SERA_REVERTIDA (ID INTEGER);");

            var worker = NovoWorker(junior.CaminhoArquivo, bexe.CaminhoArquivo, tempPath, out var databaseService);

            // Não relança -- o catch de ProcessarAtualizacao trata a falha e grava ERRO.
            await worker.ProcessarAtualizacao(CancellationToken.None);

            Assert.Equal("ERRO", databaseService.GetStatusAtualizacao(junior.CaminhoArquivo));
            Assert.Equal("1.0.0", databaseService.GetVersaoConfirmada(junior.CaminhoArquivo), ignoreCase: true);

            // O gbak -c -replace_database restaurou o banco pro estado do backup pré-atualização --
            // de antes dos scripts rodarem. Se a tabela existisse aqui, o rollback não teria
            // restaurado de verdade (ver item 3 do RISCOS-CONHECIDOS.md).
            Assert.DoesNotContain("Cria_tabela_sera_revertida.sql", databaseService.GetScriptsAplicados(junior.CaminhoArquivo));
            Assert.False(databaseService.VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE TABELA_SERA_REVERTIDA (ID INTEGER)").JaExiste);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }
}
