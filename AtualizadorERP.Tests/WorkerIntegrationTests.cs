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
    private const string Sistema = "SISTEMA_TESTE";

    // Precisa bater com Worker.PastaPacotesDoSistema ("pacotes\{sistema}", não "pacotes\" direto) --
    // ProcessarAtualizacao monta o caminho sozinho a partir do sistema, então o pacote de teste
    // tem que estar exatamente aí, senão RunPendingScriptsAsync lança DirectoryNotFoundException
    // antes mesmo de chegar nos scripts.
    private static string NovaPastaPacotes(string pastaTrabalho, string sistema = Sistema)
    {
        string pastaPacotes = Path.Combine(pastaTrabalho, "pacotes", sistema);
        Directory.CreateDirectory(pastaPacotes);
        return pastaPacotes;
    }

    private static Worker NovoWorker(string juniorPath, string bexePath, string pastaTrabalho, string pastaBackups, out DatabaseService databaseService)
    {
        var config = TestAmbiente.NovaConfiguracao(juniorFdbPath: juniorPath, bexeFdbPath: bexePath, pastaTrabalho: pastaTrabalho, pastaBackups: pastaBackups);

        databaseService = new DatabaseService(config);
        var processService = new ProcessService(NullLogger<ProcessService>.Instance);
        var apiService = new ApiService(NullLogger<ApiService>.Instance, config);
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

            await worker.ProcessarAtualizacao(Sistema, CancellationToken.None);

            Assert.Equal("CONCLUIDO", databaseService.GetStatusAtualizacao(junior.CaminhoArquivo, Sistema));
            Assert.Equal("9.9.9", databaseService.GetVersaoConfirmada(junior.CaminhoArquivo, Sistema));
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
            Assert.Contains(backupsGravados, f => Path.GetFileName(f).StartsWith($"JUNIOR_PRE_{Sistema}_9_9_9_"));
            Assert.Contains(backupsGravados, f => Path.GetFileName(f).StartsWith($"JUNIOR_POS_{Sistema}_9_9_9_"));
        }
        finally
        {
            if (Directory.Exists(pastaTrabalho)) Directory.Delete(pastaTrabalho, true);
            if (Directory.Exists(pastaBackups)) Directory.Delete(pastaBackups, true);
        }
    }

    [Fact]
    public async Task Sistema_travado_em_PROCESSANDO_e_retomado_automaticamente()
    {
        // Achado na revisão de código (2026-09-17): se o agente morre entre "gfix -shut" e "gfix
        // -online" (queda de energia, Stop-Service forçado -- não uma exceção .NET normal que o
        // catch de ProcessarAtualizacao pudesse tratar), STATUS fica "PROCESSANDO" pra sempre.
        // Antes desta correção, nenhum ramo de ProcessarSistemaAsync tratava esse status -- o
        // sistema era silenciosamente pulado em todo ciclo seguinte (caía no "return true" final,
        // sem log), e o JUNIOR.fdb podia ficar em shutdown multiusuário indefinidamente. Simula a
        // queda criando o banco já com STATUS=PROCESSANDO (em vez de AUTORIZADO) e conferindo que
        // o próprio ProcessarSistemaAsync retoma e conclui sozinho.
        using var junior = FirebirdTestDatabase.CriarJunior(status: "PROCESSANDO", versaoAtual: "1.0.0", versaoNova: "9.9.9");
        using var bexe = FirebirdTestDatabase.CriarBexe();
        string pastaTrabalho = Directory.CreateTempSubdirectory("atualizador_worker_teste_").FullName;
        string pastaBackups = Directory.CreateTempSubdirectory("atualizador_worker_backups_").FullName;
        string pastaPacotes = NovaPastaPacotes(pastaTrabalho);
        try
        {
            File.WriteAllBytes(Path.Combine(pastaPacotes, "produto_teste.exe"), new byte[] { 1, 2, 3 });

            var worker = NovoWorker(junior.CaminhoArquivo, bexe.CaminhoArquivo, pastaTrabalho, pastaBackups, out var databaseService);

            bool ok = await InvocarProcessarSistemaAsync(worker, Sistema);

            Assert.True(ok);
            Assert.Equal("CONCLUIDO", databaseService.GetStatusAtualizacao(junior.CaminhoArquivo, Sistema));
            Assert.Equal("9.9.9", databaseService.GetVersaoConfirmada(junior.CaminhoArquivo, Sistema));
        }
        finally
        {
            if (Directory.Exists(pastaTrabalho)) Directory.Delete(pastaTrabalho, true);
            if (Directory.Exists(pastaBackups)) Directory.Delete(pastaBackups, true);
        }
    }

    // ProcessarSistemaAsync é privado (decide o roteamento por STATUS: CONCLUIDO/ERRO -> checa
    // update, AUTORIZADO/PROCESSANDO -> ProcessarAtualizacao) -- via reflection só pra exercitar
    // esse roteamento sem duplicar o setup de CheckForUpdates contra uma API real.
    private static async Task<bool> InvocarProcessarSistemaAsync(Worker worker, string sistema)
    {
        var metodo = typeof(Worker).GetMethod("ProcessarSistemaAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return await (Task<bool>)metodo.Invoke(worker, new object[] { sistema, CancellationToken.None })!;
    }

    [Fact]
    public async Task Sistema_sem_script_pendente_e_aplicado_junto_quando_sistema_com_script_conclui()
    {
        // Cenário motivador: o NFe não pode trocar de executável sozinho, silenciosamente, com o
        // terminal aberto emitindo nota -- fica PENDENTE (Worker.ExisteSistemaComScriptInstalado)
        // até o B_Vendas ser autorizado. Só quando a Fase 3/4 do B_Vendas termina com sucesso
        // (aqui, direto via ProcessarAtualizacao) é que o NFe pendente é varrido e aplicado junto
        // (Worker.AplicarPendentesSemScriptAsync) -- nunca antes, pra não desencontrar versões se
        // o B_Vendas tivesse revertido.
        const string SistemaComScript = "BVENDAS_TESTE";
        const string SistemaSemScript = "NFE_TESTE";

        using var junior = FirebirdTestDatabase.CriarJunior(sistema: SistemaComScript, status: "AUTORIZADO", versaoAtual: "1.0.0", versaoNova: "2.0.0");
        junior.ExecutarNaoConsulta(
            "INSERT INTO SYS_ATUALIZACAO (SISTEMA, STATUS, VERSAO_NOVA, VERSAO_ATUAL) VALUES (@sistema, @status, @versaoNova, @versaoAtual)",
            ("@sistema", SistemaSemScript), ("@status", "PENDENTE"), ("@versaoNova", "2.0.0"), ("@versaoAtual", "1.0.0"));
        using var bexe = FirebirdTestDatabase.CriarBexe();

        string pastaTrabalho = Directory.CreateTempSubdirectory("atualizador_worker_teste_").FullName;
        string pastaBackups = Directory.CreateTempSubdirectory("atualizador_worker_backups_").FullName;
        string pastaCliente = Path.GetDirectoryName(Path.GetFullPath(bexe.CaminhoArquivo))!;

        string pastaPacotesComScript = Path.Combine(pastaTrabalho, "pacotes", SistemaComScript);
        Directory.CreateDirectory(pastaPacotesComScript);
        File.WriteAllBytes(Path.Combine(pastaPacotesComScript, "bvendas_teste.exe"), new byte[] { 1, 2, 3 });

        // Pacote do sistema sem script já baixado/extraído numa rodada anterior -- ficou PENDENTE
        // esperando o com-script, sem aplicar (ver EhSistemaComScript/ExisteSistemaComScriptInstalado).
        string pastaPacotesSemScript = Path.Combine(pastaTrabalho, "pacotes", SistemaSemScript);
        Directory.CreateDirectory(pastaPacotesSemScript);
        byte[] conteudoNfe = { 9, 8, 7, 6, 5 };
        File.WriteAllBytes(Path.Combine(pastaPacotesSemScript, "nfe_teste.exe"), conteudoNfe);

        // "Instalados" pra SistemaInstalado (presença do exe na pasta do cliente) -- placeholders
        // que a própria CopiarParaPastaCliente sobrescreve com o conteúdo real do pacote.
        File.WriteAllBytes(Path.Combine(pastaCliente, "bvendas_teste.exe"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(pastaCliente, "nfe_teste.exe"), new byte[] { 0 });

        var sistemas = new[]
        {
            new SistemaConfigurado(SistemaComScript, "bvendas_teste.exe"),
            new SistemaConfigurado(SistemaSemScript, "nfe_teste.exe"),
        };
        var config = TestAmbiente.NovaConfiguracao(
            juniorFdbPath: junior.CaminhoArquivo, bexeFdbPath: bexe.CaminhoArquivo,
            pastaTrabalho: pastaTrabalho, pastaBackups: pastaBackups,
            sistemas: sistemas, sistemasComScript: new[] { SistemaComScript });

        var databaseService = new DatabaseService(config);
        var processService = new ProcessService(NullLogger<ProcessService>.Instance);
        var apiService = new ApiService(NullLogger<ApiService>.Instance, config);
        var extractionService = new ExtractionService(NullLogger<ExtractionService>.Instance, processService);
        var scriptRunnerService = new ScriptRunnerService(NullLogger<ScriptRunnerService>.Instance, databaseService, processService, apiService, config);
        var worker = new Worker(NullLogger<Worker>.Instance, apiService, databaseService, extractionService, processService, scriptRunnerService, config);

        try
        {
            await worker.ProcessarAtualizacao(SistemaComScript, CancellationToken.None);

            Assert.Equal("CONCLUIDO", databaseService.GetStatusAtualizacao(junior.CaminhoArquivo, SistemaComScript));
            Assert.Equal("CONCLUIDO", databaseService.GetStatusAtualizacao(junior.CaminhoArquivo, SistemaSemScript));
            Assert.Equal("2.0.0", databaseService.GetVersaoConfirmada(junior.CaminhoArquivo, SistemaSemScript));

            string caminhoNfeEsperado = Path.Combine(pastaCliente, "nfe_teste.exe");
            string hashEsperado = Convert.ToHexString(SHA1.HashData(conteudoNfe));
            Assert.Equal(hashEsperado, bexe.ExecutarEscalar($"SELECT HASHEXE FROM EXECUTAVEIS WHERE NOMEARQUIVO = '{caminhoNfeEsperado}'"));
            Assert.False(Directory.Exists(pastaPacotesSemScript));
        }
        finally
        {
            if (Directory.Exists(pastaTrabalho)) Directory.Delete(pastaTrabalho, true);
            if (Directory.Exists(pastaBackups)) Directory.Delete(pastaBackups, true);
            File.Delete(Path.Combine(pastaCliente, "bvendas_teste.exe"));
            File.Delete(Path.Combine(pastaCliente, "nfe_teste.exe"));
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

            InvocarArquivarBackups(worker, "BVENDAS_TESTE", Path.Combine(pastaTrabalho, "inexistente_pre.fbk"), Path.Combine(pastaTrabalho, "inexistente_pos.fbk"), "9.9.9");

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

    [Fact]
    public void ExisteSistemaComScriptInstalado_reflete_se_o_sistema_com_script_esta_instalado_neste_cliente()
    {
        // Fallback proposital: um cliente que não tem NENHUM sistema com script instalado (só
        // distribui .exe avulso, ex.: só NFe) não tem nenhuma "janela de manutenção" pra esperar --
        // sem isso, sistemas sem script desse cliente nunca sairiam de PENDENTE.
        string pastaCliente = Directory.CreateTempSubdirectory("atualizador_worker_gating_").FullName;
        try
        {
            var sistemas = new[]
            {
                new SistemaConfigurado("BVENDAS_TESTE", "bvendas_gating.exe"),
                new SistemaConfigurado("NFE_TESTE", "nfe_gating.exe"),
            };
            string bexePath = Path.Combine(pastaCliente, "BEXE.FDB");

            // Só o NFe instalado (exe presente), B_Vendas configurado mas ausente deste cliente --
            // sem sistema com script instalado, deve aplicar direto (false).
            File.WriteAllBytes(Path.Combine(pastaCliente, "nfe_gating.exe"), new byte[] { 0 });
            var configSoNfe = TestAmbiente.NovaConfiguracao(bexeFdbPath: bexePath, sistemas: sistemas, sistemasComScript: new[] { "BVENDAS_TESTE" });
            Assert.False(InvocarExisteSistemaComScriptInstalado(NovoWorkerParaPodar(configSoNfe)));

            // Com o B_Vendas também instalado, o NFe deste mesmo cliente passa a esperar (true).
            File.WriteAllBytes(Path.Combine(pastaCliente, "bvendas_gating.exe"), new byte[] { 0 });
            var configComBVendas = TestAmbiente.NovaConfiguracao(bexeFdbPath: bexePath, sistemas: sistemas, sistemasComScript: new[] { "BVENDAS_TESTE" });
            Assert.True(InvocarExisteSistemaComScriptInstalado(NovoWorkerParaPodar(configComBVendas)));
        }
        finally
        {
            Directory.Delete(pastaCliente, true);
        }
    }

    // ExisteSistemaComScriptInstalado é privado (detalhe de implementação de ProcessarSistemaAsync,
    // que por sua vez depende de ApiService.CheckForUpdates contra um servidor real -- inviável de
    // testar de ponta a ponta aqui) -- via reflection só neste teste focado na decisão de gating.
    private static bool InvocarExisteSistemaComScriptInstalado(Worker worker)
    {
        var metodo = typeof(Worker).GetMethod("ExisteSistemaComScriptInstalado", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (bool)metodo.Invoke(worker, null)!;
    }

    private static Worker NovoWorkerParaPodar(ConfiguracaoAgente config)
    {
        var databaseService = new DatabaseService(config);
        var processService = new ProcessService(NullLogger<ProcessService>.Instance);
        var apiService = new ApiService(NullLogger<ApiService>.Instance, config);
        var extractionService = new ExtractionService(NullLogger<ExtractionService>.Instance, processService);
        var scriptRunnerService = new ScriptRunnerService(NullLogger<ScriptRunnerService>.Instance, databaseService, processService, apiService, config);
        return new Worker(NullLogger<Worker>.Instance, apiService, databaseService, extractionService, processService, scriptRunnerService, config);
    }

    // ArquivarBackups é privado (detalhe de implementação de ProcessarAtualizacao) -- via
    // reflection só neste teste focado na poda, pra não precisar rodar o ciclo completo (gfix/
    // gbak/scripts reais) só pra testar "mantém os últimos N arquivos".
    private static void InvocarArquivarBackups(Worker worker, string sistema, string preBkp, string posBkp, string versaoAlvo)
    {
        var metodo = typeof(Worker).GetMethod("ArquivarBackups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        metodo.Invoke(worker, new object[] { sistema, preBkp, posBkp, versaoAlvo });
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
            await worker.ProcessarAtualizacao(Sistema, CancellationToken.None);

            Assert.Equal("ERRO", databaseService.GetStatusAtualizacao(junior.CaminhoArquivo, Sistema));
            Assert.Equal("1.0.0", databaseService.GetVersaoConfirmada(junior.CaminhoArquivo, Sistema), ignoreCase: true);

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
