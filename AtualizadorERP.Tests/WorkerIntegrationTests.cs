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
    private static string NovaPastaPacotes(string pastaTrabalho)
    {
        string pastaPacotes = Path.Combine(pastaTrabalho, "pacotes");
        Directory.CreateDirectory(pastaPacotes);
        return pastaPacotes;
    }

    private static Worker NovoWorker(string juniorPath, string bexePath, string pastaTrabalho, string pastaBackups, out DatabaseService databaseService)
    {
        var config = TestAmbiente.NovaConfiguracao(juniorFdbPath: juniorPath, bexeFdbPath: bexePath, pastaTrabalho: pastaTrabalho, pastaBackups: pastaBackups);

        databaseService = new DatabaseService(config);
        var processService = new ProcessService(NullLogger<ProcessService>.Instance);
        var apiService = new ApiService(config);
        var extractionService = new ExtractionService(NullLogger<ExtractionService>.Instance, processService);
        var scriptRunnerService = new ScriptRunnerService(NullLogger<ScriptRunnerService>.Instance, databaseService, processService, apiService, config);

        return new Worker(NullLogger<Worker>.Instance, apiService, databaseService, extractionService, processService, scriptRunnerService, config);
    }

    [Fact]
    public async Task Ciclo_completo_com_sucesso_promove_versao_injeta_binario_e_arquiva_backups()
    {
        using var junior = FirebirdTestDatabase.CriarJunior(status: "AUTORIZADO", versaoAtual: "1.0.0", versaoNova: "9.9.9");
        using var bexe = FirebirdTestDatabase.CriarBexe();
        string pastaTrabalho = Directory.CreateTempSubdirectory("atualizador_worker_teste_").FullName;
        string pastaBackups = Directory.CreateTempSubdirectory("atualizador_worker_backups_").FullName;
        string pastaPacotes = NovaPastaPacotes(pastaTrabalho);
        try
        {
            byte[] conteudoExe = { 1, 2, 3, 4, 5, 6, 7 };
            File.WriteAllBytes(Path.Combine(pastaPacotes, "produto_teste.exe"), conteudoExe);
            File.WriteAllText(Path.Combine(pastaPacotes, "Cria_tabela_teste.sql"), "CREATE TABLE TABELA_CICLO_COMPLETO (ID INTEGER);");

            var worker = NovoWorker(junior.CaminhoArquivo, bexe.CaminhoArquivo, pastaTrabalho, pastaBackups, out var databaseService);

            await worker.ProcessarAtualizacao(CancellationToken.None);

            Assert.Equal("CONCLUIDO", databaseService.GetStatusAtualizacao(junior.CaminhoArquivo));
            Assert.Equal("9.9.9", databaseService.GetVersaoConfirmada(junior.CaminhoArquivo));
            Assert.Contains("Cria_tabela_teste.sql", databaseService.GetScriptsAplicados(junior.CaminhoArquivo));
            Assert.True(databaseService.VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE TABELA_CICLO_COMPLETO (ID INTEGER)").JaExiste);

            // Formato confirmado contra BEXE_certo.FDB (03/09/2026): NOMEARQUIVO é o caminho
            // completo (pasta do BEXE.fdb + nome), HASHEXE é SHA-1 maiúsculo, VERSAOATUALIZADA é
            // sempre "True", e VERSAO cai pra versaoNova quando o exe (fake, neste teste) não tem
            // FileVersion embutido.
            string caminhoExeEsperado = Path.Combine(Path.GetDirectoryName(bexe.CaminhoArquivo)!, "produto_teste.exe");
            string hashEsperado = Convert.ToHexString(SHA1.HashData(conteudoExe));
            Assert.Equal(hashEsperado, bexe.ExecutarEscalar($"SELECT HASHEXE FROM EXECUTAVEIS WHERE NOMEARQUIVO = '{caminhoExeEsperado}'"));
            Assert.Equal("True", bexe.ExecutarEscalar($"SELECT VERSAOATUALIZADA FROM EXECUTAVEIS WHERE NOMEARQUIVO = '{caminhoExeEsperado}'"));
            Assert.Equal("9.9.9", bexe.ExecutarEscalar($"SELECT VERSAO FROM EXECUTAVEIS WHERE NOMEARQUIVO = '{caminhoExeEsperado}'"));

            // Caminho de sucesso apaga só a pasta de pacotes -- nada de pacote da versão anterior
            // sobra pra próxima tentativa confundir com (ver itens 2 e 3 do RISCOS-CONHECIDOS.md).
            Assert.False(Directory.Exists(pastaPacotes));

            // Backups pré/pós foram arquivados em PastaBackups (não apagados) -- é o ponto central
            // do pedido que motivou essa mudança: um backup que morre no mesmo ciclo que nasce não
            // serve pra nada em caso de precisar restaurar depois.
            var backupsGravados = Directory.GetFiles(pastaBackups, "*.fbk");
            Assert.Contains(backupsGravados, f => Path.GetFileName(f).StartsWith("JUNIOR_PRE_9_9_9_"));
            Assert.Contains(backupsGravados, f => Path.GetFileName(f).StartsWith("JUNIOR_POS_9_9_9_"));
        }
        finally
        {
            if (Directory.Exists(pastaTrabalho)) Directory.Delete(pastaTrabalho, true);
            if (Directory.Exists(pastaBackups)) Directory.Delete(pastaBackups, true);
        }
    }

    [Fact]
    public void ArquivarBackups_mantem_so_os_ultimos_N_ciclos()
    {
        // Sem limpeza, cada atualização bem-sucedida deixaria 2 backups novos (pré + pós) parados
        // pra sempre -- num cliente real, o JUNIOR.fdb pode ter centenas de MB/GB por cópia.
        using var junior = FirebirdTestDatabase.CriarJunior(status: "AUTORIZADO", versaoAtual: "1.0.0", versaoNova: "1.0.1");
        using var bexe = FirebirdTestDatabase.CriarBexe();
        string pastaTrabalho = Directory.CreateTempSubdirectory("atualizador_worker_teste_").FullName;
        string pastaBackups = Directory.CreateTempSubdirectory("atualizador_worker_backups_").FullName;
        try
        {
            // Simula 3 ciclos de backup já arquivados antes deste teste, com timestamps
            // crescentes garantidos por sufixo (o nome do arquivo já embute o timestamp, então a
            // ordenação por nome/data de criação bate).
            for (int i = 0; i < 3; i++)
            {
                File.WriteAllText(Path.Combine(pastaBackups, $"JUNIOR_PRE_1_0_{i}_2026090{i + 1}_120000.fbk"), "conteudo");
                File.WriteAllText(Path.Combine(pastaBackups, $"JUNIOR_POS_1_0_{i}_2026090{i + 1}_120000.fbk"), "conteudo");
                Thread.Sleep(10);
            }

            var config = TestAmbiente.NovaConfiguracao(pastaBackups: pastaBackups, backupsParaManter: 2);
            var worker = NovoWorkerParaPodar(config);

            InvocarArquivarBackups(worker, Path.Combine(pastaTrabalho, "inexistente_pre.fbk"), Path.Combine(pastaTrabalho, "inexistente_pos.fbk"), "9.9.9");

            // 2 ciclos mantidos = 4 arquivos (pré+pós cada), os 3 mais antigos (do loop acima)
            // descartados, restando só os 2 mais recentes dele.
            var restantes = Directory.GetFiles(pastaBackups, "*.fbk");
            Assert.Equal(4, restantes.Length);
            Assert.DoesNotContain(restantes, f => Path.GetFileName(f).Contains("1_0_0_"));
        }
        finally
        {
            if (Directory.Exists(pastaTrabalho)) Directory.Delete(pastaTrabalho, true);
            if (Directory.Exists(pastaBackups)) Directory.Delete(pastaBackups, true);
        }
    }

    private static Worker NovoWorkerParaPodar(ConfiguracaoAgente config)
    {
        var databaseService = new DatabaseService(config);
        var processService = new ProcessService(NullLogger<ProcessService>.Instance);
        var apiService = new ApiService(config);
        var extractionService = new ExtractionService(NullLogger<ExtractionService>.Instance, processService);
        var scriptRunnerService = new ScriptRunnerService(NullLogger<ScriptRunnerService>.Instance, databaseService, processService, apiService, config);
        return new Worker(NullLogger<Worker>.Instance, apiService, databaseService, extractionService, processService, scriptRunnerService, config);
    }

    // ArquivarBackups é privado (detalhe de implementação de ProcessarAtualizacao) -- via
    // reflection só neste teste focado na poda, pra não precisar rodar o ciclo completo (gfix/
    // gbak/scripts reais) só pra testar "mantém os últimos N arquivos".
    private static void InvocarArquivarBackups(Worker worker, string preBkp, string posBkp, string versaoAlvo)
    {
        var metodo = typeof(Worker).GetMethod("ArquivarBackups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        metodo.Invoke(worker, new object[] { preBkp, posBkp, versaoAlvo });
    }

    [Fact]
    public async Task Falha_na_fase_4_reverte_o_banco_via_backup_e_nao_promove_versao()
    {
        // Pacote sem nenhum .exe -- InjetarNovosBinarios lança de propósito (ver
        // DatabaseService), depois que o gfix -shut, o gbak pré e os scripts já rodaram. É o
        // cenário exato pra testar o rollback: precisa restaurar um banco que já tinha mudado de
        // estado.
        using var junior = FirebirdTestDatabase.CriarJunior(status: "AUTORIZADO", versaoAtual: "1.0.0", versaoNova: "2.0.0");
        using var bexe = FirebirdTestDatabase.CriarBexe();
        string pastaTrabalho = Directory.CreateTempSubdirectory("atualizador_worker_teste_").FullName;
        string pastaBackups = Directory.CreateTempSubdirectory("atualizador_worker_backups_").FullName;
        string pastaPacotes = NovaPastaPacotes(pastaTrabalho);
        try
        {
            File.WriteAllText(Path.Combine(pastaPacotes, "Cria_tabela_sera_revertida.sql"), "CREATE TABLE TABELA_SERA_REVERTIDA (ID INTEGER);");

            var worker = NovoWorker(junior.CaminhoArquivo, bexe.CaminhoArquivo, pastaTrabalho, pastaBackups, out var databaseService);

            // Não relança -- o catch de ProcessarAtualizacao trata a falha e grava ERRO.
            await worker.ProcessarAtualizacao(CancellationToken.None);

            Assert.Equal("ERRO", databaseService.GetStatusAtualizacao(junior.CaminhoArquivo));
            Assert.Equal("1.0.0", databaseService.GetVersaoConfirmada(junior.CaminhoArquivo), ignoreCase: true);

            // O gbak -c -replace_database restaurou o banco pro estado do backup pré-atualização --
            // de antes dos scripts rodarem. Se a tabela existisse aqui, o rollback não teria
            // restaurado de verdade (ver item 3 do RISCOS-CONHECIDOS.md).
            Assert.DoesNotContain("Cria_tabela_sera_revertida.sql", databaseService.GetScriptsAplicados(junior.CaminhoArquivo));
            Assert.False(databaseService.VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE TABELA_SERA_REVERTIDA (ID INTEGER)").JaExiste);

            // Falha não arquiva backup nenhum -- só o caminho de sucesso chama ArquivarBackups.
            Assert.Empty(Directory.GetFiles(pastaBackups, "*.fbk"));
        }
        finally
        {
            if (Directory.Exists(pastaTrabalho)) Directory.Delete(pastaTrabalho, true);
            if (Directory.Exists(pastaBackups)) Directory.Delete(pastaBackups, true);
        }
    }
}
