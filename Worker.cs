using AtualizadorERP.Services;

namespace AtualizadorERP;

// (System.Linq.Concat/ToArray usados abaixo vêm do GlobalUsings gerado pelo SDK do projeto,
// que já inclui "global using System.Linq;" para projetos .NET 8 com ImplicitUsings habilitado.)

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ApiService _apiService;
    private readonly DatabaseService _databaseService;
    private readonly ExtractionService _extractionService;
    private readonly ProcessService _processService;
    private readonly ScriptRunnerService _scriptRunnerService;

    private readonly string _cnpjCliente = Environment.GetEnvironmentVariable("ATUALIZADOR_CNPJ") ?? "";
    // Qual sistema (do catálogo em "sistemas", o mesmo da aba Sistemas do painel web) esta
    // instância do agente atualiza -- ex.: "B_Vendas". Cada JUNIOR.fdb/BEXE.fdb pertence a um
    // sistema só, então uma instância do agente cuida de um sistema só. Obrigatória: sem ela, a
    // única alternativa seria voltar ao comportamento antigo (perguntar "qual a última versão de
    // QUALQUER coisa", que já causou um agente instalar o pacote errado -- ver
    // web/docs/REVISAO_INTERFACE.md).
    private readonly string _sistema = Environment.GetEnvironmentVariable("ATUALIZADOR_SISTEMA") ?? "";
    private readonly string _tempPath = Environment.GetEnvironmentVariable("ATUALIZADOR_TEMP_PATH") ?? @"C:\TempUpdates";
    private readonly string _juniorFdbPath = Environment.GetEnvironmentVariable("ATUALIZADOR_JUNIOR_FDB") ?? @"C:\ERP\JUNIOR.fdb";
    private readonly string _bexeFdbPath = Environment.GetEnvironmentVariable("ATUALIZADOR_BEXE_FDB") ?? @"C:\ERP\BEXE.fdb";
    private readonly string _gfixPath = Environment.GetEnvironmentVariable("ATUALIZADOR_GFIX_PATH") ?? @"C:\Program Files (x86)\Firebird\Firebird_2_5\bin\gfix.exe";
    private readonly string _gbakPath = Environment.GetEnvironmentVariable("ATUALIZADOR_GBAK_PATH") ?? @"C:\Program Files (x86)\Firebird\Firebird_2_5\bin\gbak.exe";
    private readonly string _dbUser = Environment.GetEnvironmentVariable("ATUALIZADOR_DB_USER") ?? "SYSDBA";
    private readonly string _dbPassword = Environment.GetEnvironmentVariable("ATUALIZADOR_DB_PASSWORD") ?? "";
    private readonly string _dbPort = Environment.GetEnvironmentVariable("ATUALIZADOR_DB_PORT") ?? "3050";

    private static readonly TimeSpan GfixTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GbakTimeout = TimeSpan.FromMinutes(15);

    private int _falhasConsecutivas = 0;
    private bool _schemaJuniorGarantido = false;

    public Worker(ILogger<Worker> logger, ApiService apiService, DatabaseService databaseService, ExtractionService extractionService, ProcessService processService, ScriptRunnerService scriptRunnerService)
    {
        _logger = logger;
        _apiService = apiService;
        _databaseService = databaseService;
        _extractionService = extractionService;
        _processService = processService;
        _scriptRunnerService = scriptRunnerService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Fica dentro do try/catch de propósito: se o JUNIOR.fdb não estiver acessível
                // ainda no arranque do serviço, isso deve entrar no mesmo backoff de qualquer
                // outra falha do ciclo, não derrubar o Worker. Só marca "garantido" depois de um
                // sucesso de verdade, então uma falha aqui tenta de novo no próximo ciclo.
                if (!_schemaJuniorGarantido)
                {
                    _databaseService.GarantirTabelaSysAtualizacao(_juniorFdbPath);
                    _schemaJuniorGarantido = true;
                }

                var statusAtual = _databaseService.GetStatusAtualizacao(_juniorFdbPath);
                if (statusAtual == "CONCLUIDO" || statusAtual == "ERRO")
                {
                    if (string.IsNullOrWhiteSpace(_cnpjCliente))
                        throw new InvalidOperationException("Defina ATUALIZADOR_CNPJ antes de iniciar o agente.");
                    if (string.IsNullOrWhiteSpace(_sistema))
                        throw new InvalidOperationException("Defina ATUALIZADOR_SISTEMA antes de iniciar o agente.");

                    // VERSAO_ATUAL, não VERSAO_NOVA: é a última versão CONFIRMADA (só muda depois
                    // de uma Fase 3 com sucesso de verdade, ver ConfirmarVersaoAtual). Continua
                    // valendo mesmo que uma tentativa anterior tenha falhado no meio -- por isso
                    // não precisa de um arquivo solto fora do banco pra "lembrar" pra onde reverter.
                    string versaoAtual = _databaseService.GetVersaoConfirmada(_juniorFdbPath);
                    var updateInfo = await _apiService.CheckForUpdates(_cnpjCliente, _sistema, versaoAtual);
                    if (updateInfo?.HasUpdate == true)
                    {
                        // Começa de uma pasta vazia: o _tempPath só é limpo no caminho de
                        // sucesso, então sobras de uma tentativa anterior que falhou ainda
                        // estariam aqui. Como a Fase 4 injeta no BEXE tudo que for "*.exe"
                        // desta pasta, um executável remanescente de outra versão entraria
                        // junto com os desta -- misturando binários de versões diferentes
                        // nos terminais.
                        if (Directory.Exists(PastaPacotes)) Directory.Delete(PastaPacotes, true);
                        Directory.CreateDirectory(PastaPacotes);
                        var baixados = await _apiService.DownloadPackages(updateInfo.Packages, PastaPacotes, stoppingToken);
                        await _extractionService.ExtractAllAsync(baixados, PastaPacotes, stoppingToken);

                        _databaseService.SetStatusAtualizacao(_juniorFdbPath, "PENDENTE", updateInfo.Version);
                    }

                    // Só zera aqui se já estava "CONCLUIDO" -- só polling HTTP rodou, de fato
                    // saudável. Vindo de "ERRO", zerar aqui apagaria o backoff da falha da Fase 3
                    // assim que o próximo ciclo re-enfileirasse a MESMA atualização como PENDENTE:
                    // o agente voltaria a bater a cada 10s numa atualização que já sabemos que
                    // falha, e o backoff só protegeria contra falha de download, nunca de
                    // atualização (só ProcessarAtualizacao pode zerar depois de um sucesso real).
                    if (statusAtual == "CONCLUIDO") _falhasConsecutivas = 0;
                }
                else if (statusAtual == "AUTORIZADO")
                {
                    // Gerencia o próprio contador de falhas -- não relança, então não deve
                    // ser tratado aqui como "ciclo saudável" automaticamente.
                    await ProcessarAtualizacao(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _falhasConsecutivas++;
                _logger.LogError(ex, "Erro no ciclo do Worker.");
            }

            await Task.Delay(ProximoIntervalo(), stoppingToken);
        }
    }

    // Só o conteúdo dos pacotes da versão fica aqui -- e é exatamente esta pasta que a Fase 4
    // varre atrás de "*.exe" para injetar no BEXE.fdb, e que o ScriptRunnerService varre atrás
    // de "*.sql" para aplicar na Fase 3. Os backups do gbak ficam de fora, na raiz de _tempPath.
    // A varredura não pode ser substituída por uma lista em memória porque Fase 1 e Fase 4
    // acontecem em ciclos diferentes (podendo ter um reinício do serviço no meio), então quem
    // separa é o layout de pastas.
    private string PastaPacotes => Path.Combine(_tempPath, "pacotes");

    // Backoff simples: 10s no caminho saudável; cresce até 30 minutos em falhas seguidas, para
    // não martelar disco/rede/API a cada 10 segundos quando algo está persistentemente quebrado
    // (ex.: disco cheio, permissão negada, credencial errada).
    private TimeSpan ProximoIntervalo()
    {
        if (_falhasConsecutivas <= 0) return TimeSpan.FromSeconds(10);
        double minutos = Math.Min(30, Math.Pow(2, _falhasConsecutivas - 1));
        return TimeSpan.FromMinutes(minutos);
    }

    // internal, não private: permite o teste de integração de ponta a ponta (Fase 3/4 contra
    // Firebird real) chamar exatamente o mesmo caminho de código do serviço, em vez de duplicar a
    // orquestração no teste -- ver AtualizadorERP.Tests/WorkerIntegrationTests.cs.
    internal async Task ProcessarAtualizacao(CancellationToken stoppingToken)
    {
        string preBkp = Path.Combine(_tempPath, "JUNIOR_PRE.fbk");
        if (string.IsNullOrWhiteSpace(_dbPassword))
            throw new InvalidOperationException("Defina ATUALIZADOR_DB_PASSWORD antes de atualizar o banco.");

        // ISC_USER/ISC_PASSWORD via ambiente do processo, não "-user"/"-password" na linha de
        // comando: qualquer processo local lê a linha de comando de outro via Gerenciador de
        // Tarefas ou WMI, mas não o ambiente de um processo alheio.
        var credenciaisEnv = new Dictionary<string, string> { ["ISC_USER"] = _dbUser, ["ISC_PASSWORD"] = _dbPassword };

        // "localhost/{porta}:", não o caminho puro -- testado que gfix/gbak com caminho puro
        // resolvem pelo provedor local/XNET, que numa máquina com mais de uma versão do Firebird
        // instalada pode não ser a mesma instância que ATUALIZADOR_DB_PORT aponta (achado
        // testando: caminho puro caiu numa instância de ODS mais antigo, "unsupported on-disk
        // structure"). Consistente com o que DatabaseService já faz para toda conexão via
        // FbConnection.
        string alvoJunior = $"localhost/{_dbPort}:{_juniorFdbPath}";

        // Lidas ANTES do shutdown (gfix -shut, logo abaixo): depois dele qualquer conexão nova
        // fica bloqueada até o "-online" (sucesso ou falha), e o SendLog de resultado -- dando
        // certo ou errado -- precisa das duas pra reportar "de X para Y" ao painel de Distribuição.
        // "versaoAlvo" é VERSAO_NOVA, já definida no banco desde a Fase 1 (quando o Worker achou a
        // atualização e chamou SetStatusAtualizacao com o Version do CheckForUpdates) -- por isso
        // continua disponível mesmo que esta tentativa falhe antes de instalar nada.
        string versaoAnterior = _databaseService.GetVersaoConfirmada(_juniorFdbPath);
        string versaoAlvo = _databaseService.GetVersaoAtual(_juniorFdbPath);
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        // Só um backup gerado com sucesso NESTA tentativa pode ser restaurado.
        //
        // O _tempPath só é apagado no caminho de sucesso, então um
        // JUNIOR_PRE.fbk pode ter sobrado de uma tentativa anterior que falhou.
        // Se esta tentativa quebrar ANTES de gerar o backup novo -- no
        // "gfix -shut" logo abaixo, por exemplo -- o catch encontraria aquele
        // arquivo velho e restauraria o banco para o estado de horas ou dias
        // atrás, apagando tudo que o cliente movimentou desde então. O mesmo
        // valia para um .fbk truncado por um gbak que falhou no meio.
        bool backupValido = false;
        try
        {
            if (File.Exists(preBkp)) File.Delete(preBkp);

            _databaseService.SetStatusAtualizacao(_juniorFdbPath, "PROCESSANDO", null);
            // "multi" (manutenção multiusuário), não "full": testado que "full" bloqueia até o
            // SYSDBA -- o isql do ScriptRunnerService (linha abaixo) nunca conseguiria conectar
            // pra aplicar os scripts. "multi" isola os terminais do ERP (usuários comuns) e ainda
            // permite conexão administrativa, que é o que a Fase 3 precisa. Também corrigido:
            // "-shut force_0" (um token só) nunca foi sintaxe válida do gfix -- testado que dá
            // "Target shutdown mode is invalid"; o certo é "-shut <modo> -force <segundos>"
            // como dois parâmetros separados.
            await _processService.RunProcessAsync(_gfixPath, new[] { "-shut", "multi", "-force", "0", alvoJunior }, GfixTimeout, stoppingToken, credenciaisEnv);

            await _processService.RunProcessAsync(_gbakPath, new[] { "-b", alvoJunior, preBkp }, GbakTimeout, stoppingToken, credenciaisEnv);
            backupValido = true;

            int scriptsComFalha = await _scriptRunnerService.RunPendingScriptsAsync(_juniorFdbPath, PastaPacotes, _cnpjCliente, _sistema, stoppingToken);

            // "versaoAlvo", não uma nova leitura de VERSAO_NOVA: o valor não muda durante o
            // processamento (só GetVersaoConfirmada/VERSAO_ATUAL avança, e só depois do sucesso
            // completo, em ConfirmarVersaoAtual abaixo) -- reler seria uma consulta a mais no banco
            // pra buscar exatamente o mesmo valor já lido antes do shutdown.
            _databaseService.InjetarNovosBinarios(_bexeFdbPath, PastaPacotes, versaoAlvo);
            await _processService.RunProcessAsync(_gfixPath, new[] { "-online", alvoJunior }, GfixTimeout, stoppingToken, credenciaisEnv);

            // Backup pós-atualização depois do "-online", não antes: nada lê o JUNIOR_POS.fbk
            // (é apagado poucas linhas abaixo, no Directory.Delete(_tempPath, true)), então ele
            // não tinha motivo pra estar dentro da janela de shutdown -- só somava até 15 min de
            // indisponibilidade jogada fora. Com o banco já online, essa cópia roda sem afetar
            // os terminais do ERP.
            string posBkp = Path.Combine(_tempPath, "JUNIOR_POS.fbk");
            await _processService.RunProcessAsync(_gbakPath, new[] { "-b", alvoJunior, posBkp }, GbakTimeout, stoppingToken, credenciaisEnv);

            // VERSAO_ATUAL só avança pra VERSAO_NOVA aqui -- na Fase 3 concluída de verdade. Se
            // qualquer passo acima (gfix/gbak/scripts/injeção) tivesse lançado, essa linha nunca
            // roda e VERSAO_ATUAL continua no valor de antes, sem precisar reverter nada.
            _databaseService.ConfirmarVersaoAtual(_juniorFdbPath);

            // "CONCLUIDO" mesmo com scripts pulados: cada um já foi reportado à API na hora, pelo
            // próprio ScriptRunnerService, e não faz sentido reverter os milhares que aplicaram
            // certo por causa de um punhado de scripts legados com nome divergente do schema real.
            string mensagemFinal = scriptsComFalha > 0
                ? $"Atualização concluída com {scriptsComFalha} script(s) pulado(s) por erro -- ver detalhes nos retornos individuais."
                : "Atualização concluída com sucesso.";
            _databaseService.SetStatusAtualizacao(_juniorFdbPath, "CONCLUIDO", null, scriptsComFalha > 0 ? mensagemFinal : null);
            await _apiService.SendLog(_cnpjCliente, _sistema, "SUCESSO", mensagemFinal, versaoAlvo, versaoAnterior, cronometro.Elapsed);
            if (Directory.Exists(_tempPath)) Directory.Delete(_tempPath, true);
            _falhasConsecutivas = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha crítica durante atualização.");
            try
            {
                if (backupValido && File.Exists(preBkp))
                {
                    await _processService.RunProcessAsync(_gbakPath, new[] { "-c", "-replace_database", preBkp, alvoJunior }, GbakTimeout, stoppingToken, credenciaisEnv);
                }
                // Testado: depois de "-c -replace_database", o banco resultante já fica acessível
                // sozinho -- esse "-online" aqui frequentemente falha com "Target shutdown mode is
                // invalid" (não há shutdown nenhum pra tirar), mesmo o banco já estando utilizável.
                // Por isso fica dentro do try/catch: uma falha aqui não indica necessariamente que
                // o banco ficou inacessível, só que não havia shutdown ativo pra desfazer.
                await _processService.RunProcessAsync(_gfixPath, new[] { "-online", alvoJunior }, GfixTimeout, stoppingToken, credenciaisEnv);
            }
            catch (Exception onlineError)
            {
                _logger.LogError(onlineError, "gfix -online falhou após a falha original -- pode só significar que o banco já não estava em shutdown (comum após um restore).");
            }

            // Sem revert de versão pra fazer aqui: VERSAO_ATUAL só é avançada em
            // ConfirmarVersaoAtual, no caminho de sucesso -- se caiu aqui, ela nunca mudou.
            _databaseService.SetStatusAtualizacao(_juniorFdbPath, "ERRO", null, ex.Message);
            // "versaoAlvo" aqui é a versão que esta tentativa buscava e NÃO alcançou (o rollback
            // acima já devolveu o banco pro estado de "versaoAnterior") -- é o que o painel precisa
            // pra mostrar "tentou ir pra 2026.09.01, falhou, continua na 2026.08.27".
            await _apiService.SendLog(_cnpjCliente, _sistema, "ERRO", ex.Message, versaoAlvo, versaoAnterior, cronometro.Elapsed);
            _falhasConsecutivas++;
        }
    }
}
