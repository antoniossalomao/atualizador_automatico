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
    private readonly ConfiguracaoAgente _config;

    private static readonly TimeSpan GfixTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GbakTimeout = TimeSpan.FromMinutes(15);

    private int _falhasConsecutivas = 0;

    // Sistemas cuja linha em SYS_ATUALIZACAO já foi garantida nesta execução do serviço -- não
    // são todos os SistemaConfigurado (ver ConfiguracaoAgente.Sistemas), só os que já foram
    // detectados como instalados neste cliente ao menos uma vez. Um HashSet em vez de um bool
    // único porque a detecção (SistemaInstalado) é reavaliada a cada ciclo -- um sistema instalado
    // DEPOIS que o serviço já estava rodando precisa ganhar sua linha na primeira vez que aparecer,
    // sem reiniciar o agente.
    private readonly HashSet<string> _sistemasComLinhaGarantida = new(StringComparer.OrdinalIgnoreCase);

    public Worker(ILogger<Worker> logger, ApiService apiService, DatabaseService databaseService, ExtractionService extractionService, ProcessService processService, ScriptRunnerService scriptRunnerService, ConfiguracaoAgente config)
    {
        _logger = logger;
        _apiService = apiService;
        _databaseService = databaseService;
        _extractionService = extractionService;
        _processService = processService;
        _scriptRunnerService = scriptRunnerService;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool cicloSaudavel = true;

            // Uma instância conhece TODOS os sistemas que a empresa distribui (ver
            // ConfiguracaoAgente.Sistemas), mas só processa os que ESTE cliente realmente tem
            // instalado -- checado a cada ciclo pela presença do executável esperado na pasta do
            // cliente (ao lado do BEXE.fdb), não por configuração fixa. Sem esse filtro, publicar
            // uma versão nova de QUALQUER sistema faria todo cliente tentar baixá-la, mesmo quem
            // nunca teve aquele sistema.
            foreach (var sistemaConfigurado in _config.Sistemas)
            {
                if (!SistemaInstalado(sistemaConfigurado)) continue;

                try
                {
                    if (!_sistemasComLinhaGarantida.Contains(sistemaConfigurado.Nome))
                    {
                        _databaseService.GarantirTabelaSysAtualizacao(_config.JuniorFdbPath, new[] { sistemaConfigurado.Nome });
                        _sistemasComLinhaGarantida.Add(sistemaConfigurado.Nome);
                    }

                    // Cada sistema processa isolado (try/catch próprio dentro de
                    // ProcessarSistemaAsync) -- uma falha num sistema não pode impedir os outros de
                    // serem verificados no mesmo ciclo.
                    bool ok = await ProcessarSistemaAsync(sistemaConfigurado.Nome, stoppingToken);
                    cicloSaudavel &= ok;
                }
                catch (Exception ex)
                {
                    cicloSaudavel = false;
                    _logger.LogError(ex, "Erro garantindo schema para o sistema {sistema}.", sistemaConfigurado.Nome);
                }
            }

            _falhasConsecutivas = cicloSaudavel ? 0 : _falhasConsecutivas + 1;
            await Task.Delay(ProximoIntervalo(), stoppingToken);
        }
    }

    // Pasta do cliente (ex.: "Bredas\") -- a mesma referência que DatabaseService.InjetarNovosBinarios
    // usa pra montar EXECUTAVEIS.NOMEARQUIVO: onde o BEXE.fdb está, não onde o agente está.
    private string PastaCliente => Path.GetDirectoryName(Path.GetFullPath(_config.BexeFdbPath))
        ?? throw new InvalidOperationException($"Não consegui determinar a pasta do cliente a partir de {_config.BexeFdbPath}.");

    // Detecção por presença de arquivo, não por configuração: o nome do sistema no painel e o nome
    // do executável raramente batem (ex.: sistema "B_Importa", arquivo real "BImportaXML.exe"),
    // por isso ConfiguracaoAgente.Sistemas guarda o par -- aqui só confere se ESSE arquivo
    // específico existe na pasta deste cliente.
    private bool SistemaInstalado(SistemaConfigurado sistema) => File.Exists(Path.Combine(PastaCliente, sistema.NomeExeEsperado));

    /// <summary>Processa um sistema por vez: Fase 1 (checar/baixar) e, dependendo do estado atual,
    /// ou marca PENDENTE (sistemas com script, sempre; sistemas sem script também, se o cliente
    /// tiver algum sistema com script instalado -- ver <see cref="ExisteSistemaComScriptInstalado"/>)
    /// ou aplica direto (sistemas sem script, só quando não há nenhum sistema com script instalado
    /// neste cliente), ou roda a Fase 3/4 completa se já estiver AUTORIZADO -- que, ao concluir com
    /// sucesso, também aplica os sistemas sem script que ficaram PENDENTE (ver
    /// <see cref="AplicarPendentesSemScriptAsync"/>). Nunca deixa uma exceção subir: cada sistema
    /// tem seu próprio try/catch, e o retorno (sucesso/falha) só alimenta o backoff agregado do
    /// ExecuteAsync.</summary>
    private async Task<bool> ProcessarSistemaAsync(string sistema, CancellationToken stoppingToken)
    {
        try
        {
            var statusAtual = _databaseService.GetStatusAtualizacao(_config.JuniorFdbPath, sistema);
            if (statusAtual == "CONCLUIDO" || statusAtual == "ERRO")
            {
                // VERSAO_ATUAL, não VERSAO_NOVA: é a última versão CONFIRMADA (só muda depois de
                // uma atualização com sucesso de verdade, ver ConfirmarVersaoAtual).
                string versaoAtual = _databaseService.GetVersaoConfirmada(_config.JuniorFdbPath, sistema);
                var updateInfo = await _apiService.CheckForUpdates(_config.CodigoCliente, sistema, versaoAtual, stoppingToken);
                if (updateInfo?.HasUpdate == true)
                {
                    string pastaPacotes = PastaPacotesDoSistema(sistema);
                    if (Directory.Exists(pastaPacotes)) Directory.Delete(pastaPacotes, true);
                    Directory.CreateDirectory(pastaPacotes);
                    var baixados = await _apiService.DownloadPackages(updateInfo.Packages, pastaPacotes, stoppingToken);
                    await _extractionService.ExtractAllAsync(baixados, pastaPacotes, stoppingToken);

                    // "Sem script" (ex.: B_NFe) não toca no JUNIOR.fdb, mas TROCAR O EXECUTÁVEL
                    // sozinho não é inofensivo: se o cliente tiver algum sistema COM script
                    // instalado (normalmente B_Vendas), o agente espera ele ser autorizado antes de
                    // aplicar qualquer coisa -- é o único sinal que o agente tem de que o cliente
                    // coordenou uma janela de manutenção de verdade pelo painel. Aplicar direto
                    // (como antes) arriscava sobrescrever o .exe/DLLs de um terminal com o NFe
                    // aberto no meio de uma emissão de nota. Só quando NÃO existe nenhum sistema com
                    // script instalado (cliente que só distribui .exe avulso) é que ainda aplica
                    // direto -- senão esse sistema nunca teria nenhuma janela pra esperar e nunca
                    // atualizaria.
                    if (EhSistemaComScript(sistema) || ExisteSistemaComScriptInstalado())
                    {
                        _databaseService.SetStatusAtualizacao(_config.JuniorFdbPath, sistema, "PENDENTE", updateInfo.Version);
                        // Só reportado aqui, uma vez, no instante da transição -- nos ciclos
                        // seguintes o status já não bate mais com "CONCLUIDO"/"ERRO" (ver o
                        // if logo acima), então este branch não roda de novo enquanto persistir
                        // PENDENTE. Sem isso, o painel nunca sabia que um sistema estava esperando
                        // autorização -- só via "desatualizado" genérico, indistinguível de um
                        // agente que nem chegou a baixar nada.
                        await _apiService.SendLog(_config.CodigoCliente, sistema, "PENDENTE", "Atualização baixada, aguardando autorização (Fase 2) para aplicar.", versaoAtual, null, null, stoppingToken, fase: "aguardando_autorizacao");
                    }
                    else
                    {
                        return await AplicarAtualizacaoSemScriptAsync(sistema, pastaPacotes, updateInfo.Version, stoppingToken);
                    }
                }
                return true;
            }
            else if (statusAtual == "AUTORIZADO")
            {
                return await ProcessarAtualizacao(sistema, stoppingToken);
            }
            else if (statusAtual == "PROCESSANDO")
            {
                // Só fica PROCESSANDO durante a Fase 3/4 em andamento (ver ProcessarAtualizacao) --
                // se o agente chega aqui vendo esse status, é porque a tentativa anterior nunca
                // terminou de verdade (o serviço morreu no meio: crash, queda de energia, "Stop-
                // Service" forçado -- nada que passasse pelo catch normal). Sem este ramo, o
                // sistema ficava preso nesse status pra sempre (nenhum outro branch trata
                // "PROCESSANDO"), e pior: se a queda foi entre o "gfix -shut" e o "gfix -online", o
                // JUNIOR.fdb desse cliente fica em shutdown multiusuário indefinidamente, sem
                // ninguém tentando religar. ProcessarAtualizacao já é seguro de rodar de novo do
                // zero (apaga qualquer preBkp velho antes de recriar, scripts já aplicados são
                // pulados pela checagem em SCRIPTS) -- então só repetir o processo já é a própria
                // recuperação: um novo "gfix -shut" religa o fluxo normal (banco já em shutdown ou
                // não) até chegar no "gfix -online" que faltou rodar da vez anterior.
                _logger.LogWarning("Sistema {sistema} estava travado em PROCESSANDO -- provável queda do agente no meio de um ciclo anterior. Retomando.", sistema);
                return await ProcessarAtualizacao(sistema, stoppingToken);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro processando o sistema {sistema}.", sistema);
            return false;
        }
    }

    // "pacotes\{sistema}\", não "pacotes\" direto: cada sistema tem seu próprio pacote, e
    // misturar o conteúdo de dois sistemas na mesma pasta faria a Fase 4 de um injetar o
    // executável do outro junto.
    private string PastaPacotesDoSistema(string sistema) => Path.Combine(_config.PastaTrabalho, "pacotes", sistema);

    // Só os sistemas explicitamente listados em SISTEMAS_COM_SCRIPT rodam o ScriptRunnerService --
    // nunca inferido pelo conteúdo do pacote baixado. Confirmado que pacotes de outros sistemas
    // (ex.: BImportaXML) trazem .sql junto sem ser pra rodar; rodar por engano quebra o banco.
    private bool EhSistemaComScript(string sistema) => _config.SistemasComScript.Contains(sistema, StringComparer.OrdinalIgnoreCase);

    // Usado tanto pra decidir se um sistema sem script deve esperar (ver ProcessarSistemaAsync)
    // quanto, implicitamente, garante que o próprio sistema com script sendo processado já conta
    // como "instalado" (SistemaInstalado já foi checado por ele em ExecuteAsync antes de chegar
    // aqui) -- não precisa de um caso especial pra ele mesmo.
    private bool ExisteSistemaComScriptInstalado() => _config.Sistemas.Any(s => EhSistemaComScript(s.Nome) && SistemaInstalado(s));

    // Backoff simples: 10s no caminho saudável; cresce até 30 minutos em falhas seguidas, para
    // não martelar disco/rede/API a cada 10 segundos quando algo está persistentemente quebrado
    // (ex.: disco cheio, permissão negada, credencial errada).
    private TimeSpan ProximoIntervalo()
    {
        if (_falhasConsecutivas <= 0) return TimeSpan.FromSeconds(10);
        double minutos = Math.Min(30, Math.Pow(2, _falhasConsecutivas - 1));
        return TimeSpan.FromMinutes(minutos);
    }

    // Copia TUDO que veio do pacote (DLLs, pastas tipo "Schemas\", config, scripts, o próprio
    // .exe) pra dentro da pasta real do cliente, sobrescrevendo o que já existir -- não só o
    // executável que vai pro BEXE.fdb. Alguns pacotes trazem dependências que o próprio .exe do
    // ERP lê em tempo de execução (ex.: uma pasta "Schemas\" do B_NFe); sem isso, o agente
    // atualizava o BEXE.fdb (o que os TERMINAIS baixam) mas nunca atualizava a cópia real que já
    // está rodando na pasta do cliente. Roda ANTES de injetar no BEXE.fdb -- se a cópia pro disco
    // falhar (disco cheio, permissão), melhor abortar aqui do que deixar terminais baixando uma
    // versão que nem o próprio servidor conseguiu receber direito.
    private void CopiarParaPastaCliente(string pastaPacotes)
    {
        string pastaCliente = PastaCliente;
        foreach (var origem in Directory.GetFiles(pastaPacotes, "*", SearchOption.AllDirectories))
        {
            string relativo = Path.GetRelativePath(pastaPacotes, origem);
            string destino = Path.Combine(pastaCliente, relativo);
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            File.Copy(origem, destino, overwrite: true);
        }
    }

    /// <summary>Aplica uma atualização de um sistema SEM script: copia o pacote pra pasta do
    /// cliente e injeta o(s) executável(is) no BEXE.fdb -- nunca toca em gfix/gbak/JUNIOR.fdb. Não
    /// relança: trata a própria falha (grava ERRO, reporta à API) e devolve o resultado como bool,
    /// mesmo contrato de <see cref="ProcessarAtualizacao"/>.</summary>
    internal async Task<bool> AplicarAtualizacaoSemScriptAsync(string sistema, string pastaPacotes, string versaoAlvo, CancellationToken stoppingToken)
    {
        string versaoAnterior = _databaseService.GetVersaoConfirmada(_config.JuniorFdbPath, sistema);
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        string faseAtual = "copia_arquivos";
        try
        {
            _databaseService.SetStatusAtualizacao(_config.JuniorFdbPath, sistema, "PROCESSANDO", versaoAlvo);
            CopiarParaPastaCliente(pastaPacotes);
            faseAtual = "injecao_binarios";
            _databaseService.InjetarNovosBinarios(_config.BexeFdbPath, pastaPacotes, versaoAlvo);
            faseAtual = "concluido";
            _databaseService.ConfirmarVersaoAtual(_config.JuniorFdbPath, sistema);
            _databaseService.SetStatusAtualizacao(_config.JuniorFdbPath, sistema, "CONCLUIDO", null);
            await _apiService.SendLog(_config.CodigoCliente, sistema, "SUCESSO", "Atualização concluída sem script (só troca de executável).", versaoAlvo, versaoAnterior, cronometro.Elapsed, stoppingToken, fase: faseAtual);
            if (Directory.Exists(pastaPacotes)) Directory.Delete(pastaPacotes, true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha aplicando atualização sem script para {sistema}.", sistema);
            _databaseService.SetStatusAtualizacao(_config.JuniorFdbPath, sistema, "ERRO", null, ex.Message);
            await _apiService.SendLog(_config.CodigoCliente, sistema, "ERRO", ex.Message, versaoAlvo, versaoAnterior, cronometro.Elapsed, stoppingToken, fase: faseAtual);
            return false;
        }
    }

    /// <summary>Aplica todo sistema SEM script que esteja PENDENTE, exceto <paramref name="sistemaQueAcabouDeAtualizar"/>
    /// (o sistema com script que acabou de concluir e disparou esta varredura). Reaproveita
    /// exatamente o pacote já baixado/extraído em Fase 1 (nunca apagado enquanto PENDENTE, mesmo
    /// caminho que um sistema com script usa esperando autorização) -- não baixa nada de novo.
    /// Cada sistema aqui trata sua própria falha (mesmo contrato de AplicarAtualizacaoSemScriptAsync):
    /// um NFe que falhar não desfaz o B_Vendas que já concluiu, nem impede outro sistema sem script
    /// pendente de ser tentado.</summary>
    private async Task AplicarPendentesSemScriptAsync(string sistemaQueAcabouDeAtualizar, CancellationToken stoppingToken)
    {
        foreach (var sistemaConfigurado in _config.Sistemas)
        {
            string sistema = sistemaConfigurado.Nome;
            if (sistema.Equals(sistemaQueAcabouDeAtualizar, StringComparison.OrdinalIgnoreCase)) continue;
            if (EhSistemaComScript(sistema)) continue;
            if (!SistemaInstalado(sistemaConfigurado)) continue;
            if (_databaseService.GetStatusAtualizacao(_config.JuniorFdbPath, sistema) != "PENDENTE") continue;

            string versaoAlvo = _databaseService.GetVersaoAtual(_config.JuniorFdbPath, sistema);
            await AplicarAtualizacaoSemScriptAsync(sistema, PastaPacotesDoSistema(sistema), versaoAlvo, stoppingToken);
        }
    }

    // internal, não private: permite o teste de integração de ponta a ponta (Fase 3/4 contra
    // Firebird real) chamar exatamente o mesmo caminho de código do serviço, em vez de duplicar a
    // orquestração no teste -- ver AtualizadorERP.Tests/WorkerIntegrationTests.cs.
    internal async Task<bool> ProcessarAtualizacao(string sistema, CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_config.PastaTrabalho);
        string preBkp = Path.Combine(_config.PastaTrabalho, $"JUNIOR_PRE_{sistema}.fbk");

        // ISC_USER/ISC_PASSWORD via ambiente do processo, não "-user"/"-password" na linha de
        // comando: qualquer processo local lê a linha de comando de outro via Gerenciador de
        // Tarefas ou WMI, mas não o ambiente de um processo alheio.
        var credenciaisEnv = new Dictionary<string, string> { ["ISC_USER"] = _config.DbUser, ["ISC_PASSWORD"] = _config.DbPassword };

        // "localhost/{porta}:", não o caminho puro -- testado que gfix/gbak com caminho puro
        // resolvem pelo provedor local/XNET, que numa máquina com mais de uma versão do Firebird
        // instalada pode não ser a mesma instância que a porta configurada aponta (achado
        // testando: caminho puro caiu numa instância de ODS mais antigo, "unsupported on-disk
        // structure"). Consistente com o que DatabaseService já faz para toda conexão via
        // FbConnection.
        string alvoJunior = $"localhost/{_config.DbPort}:{_config.JuniorFdbPath}";

        // Lidas ANTES do shutdown (gfix -shut, logo abaixo): depois dele qualquer conexão nova
        // fica bloqueada até o "-online" (sucesso ou falha), e o SendLog de resultado -- dando
        // certo ou errado -- precisa das duas pra reportar "de X para Y" ao painel de Distribuição.
        // "versaoAlvo" é VERSAO_NOVA, já definida no banco desde a Fase 1 (quando o Worker achou a
        // atualização e chamou SetStatusAtualizacao com o Version do CheckForUpdates) -- por isso
        // continua disponível mesmo que esta tentativa falhe antes de instalar nada.
        string versaoAnterior = _databaseService.GetVersaoConfirmada(_config.JuniorFdbPath, sistema);
        string versaoAlvo = _databaseService.GetVersaoAtual(_config.JuniorFdbPath, sistema);
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        string pastaPacotes = PastaPacotesDoSistema(sistema);

        // Só um backup gerado com sucesso NESTA tentativa pode ser restaurado.
        //
        // PastaTrabalho só é apagada (a subpasta "pacotes\{sistema}", ver ArquivarBackups) no
        // caminho de sucesso, então um JUNIOR_PRE_{sistema}.fbk pode ter sobrado de uma tentativa
        // anterior que falhou. Se esta tentativa quebrar ANTES de gerar o backup novo -- no
        // "gfix -shut" logo abaixo, por exemplo -- o catch encontraria aquele arquivo velho e
        // restauraria o banco para o estado de horas ou dias atrás, apagando tudo que o cliente
        // movimentou desde então. O mesmo valia para um .fbk truncado por um gbak que falhou no
        // meio.
        bool backupValido = false;
        // Rastreia a etapa em que o ciclo está -- se cair no catch abaixo, "faseAtual" já é a
        // etapa que estava rodando quando a exceção aconteceu (não a próxima que faltava
        // alcançar), e é isso que vai pro painel de Distribuição via SendLog. Sem isso um erro
        // só dizia a mensagem crua da exceção, nunca ONDE no processo ela ocorreu.
        string faseAtual = "shutdown";
        try
        {
            if (File.Exists(preBkp)) File.Delete(preBkp);

            _databaseService.SetStatusAtualizacao(_config.JuniorFdbPath, sistema, "PROCESSANDO", null);
            // "multi" (manutenção multiusuário), não "full": testado que "full" bloqueia até o
            // SYSDBA -- o isql do ScriptRunnerService (linha abaixo) nunca conseguiria conectar
            // pra aplicar os scripts. "multi" isola os terminais do ERP e mantém acesso
            // administrativo, que é o que a Fase 3 precisa.
            await _processService.RunProcessAsync(_config.GfixPath, new[] { "-shut", "multi", "-force", "0", alvoJunior }, GfixTimeout, stoppingToken, credenciaisEnv);

            faseAtual = "backup_pre";
            await _processService.RunProcessAsync(_config.GbakPath, new[] { "-b", alvoJunior, preBkp }, GbakTimeout, stoppingToken, credenciaisEnv);
            backupValido = true;

            faseAtual = "scripts";
            int scriptsComFalha = await _scriptRunnerService.RunPendingScriptsAsync(_config.JuniorFdbPath, pastaPacotes, _config.CodigoCliente, sistema, stoppingToken);

            // "versaoAlvo", não uma nova leitura de VERSAO_NOVA: o valor não muda durante o
            // processamento (só GetVersaoConfirmada/VERSAO_ATUAL avança, e só depois do sucesso
            // completo, em ConfirmarVersaoAtual abaixo) -- reler seria uma consulta a mais no banco
            // pra buscar exatamente o mesmo valor já lido antes do shutdown.
            faseAtual = "copia_arquivos";
            CopiarParaPastaCliente(pastaPacotes);
            faseAtual = "injecao_binarios";
            _databaseService.InjetarNovosBinarios(_config.BexeFdbPath, pastaPacotes, versaoAlvo);
            faseAtual = "online";
            await _processService.RunProcessAsync(_config.GfixPath, new[] { "-online", alvoJunior }, GfixTimeout, stoppingToken, credenciaisEnv);

            // Backup pós-atualização depois do "-online", não antes: com o banco já online, o
            // gbak roda sem somar tempo à janela de indisponibilidade dos terminais do ERP.
            faseAtual = "backup_pos";
            string posBkp = Path.Combine(_config.PastaTrabalho, $"JUNIOR_POS_{sistema}.fbk");
            await _processService.RunProcessAsync(_config.GbakPath, new[] { "-b", alvoJunior, posBkp }, GbakTimeout, stoppingToken, credenciaisEnv);
            faseAtual = "concluido";

            // VERSAO_ATUAL só avança pra VERSAO_NOVA aqui -- na Fase 3 concluída de verdade. Se
            // qualquer passo acima (gfix/gbak/scripts/injeção) tivesse lançado, essa linha nunca
            // roda e VERSAO_ATUAL continua no valor de antes, sem precisar reverter nada.
            _databaseService.ConfirmarVersaoAtual(_config.JuniorFdbPath, sistema);

            // "CONCLUIDO" mesmo com scripts pulados: cada um já foi reportado à API na hora, pelo
            // próprio ScriptRunnerService, e não faz sentido reverter os milhares que aplicaram
            // certo por causa de um punhado de scripts legados com nome divergente do schema real.
            string mensagemFinal = scriptsComFalha > 0
                ? $"Atualização concluída com {scriptsComFalha} script(s) pulado(s) por erro -- ver detalhes nos retornos individuais."
                : "Atualização concluída com sucesso.";
            _databaseService.SetStatusAtualizacao(_config.JuniorFdbPath, sistema, "CONCLUIDO", null, scriptsComFalha > 0 ? mensagemFinal : null);
            await _apiService.SendLog(_config.CodigoCliente, sistema, "SUCESSO", mensagemFinal, versaoAlvo, versaoAnterior, cronometro.Elapsed, stoppingToken, fase: faseAtual);

            // A atualização em si já está confirmada e reportada como sucesso nas duas linhas
            // acima -- daqui pra baixo é só bookkeeping (arquivar backup, limpar pasta de pacotes,
            // encadear sistemas sem script pendentes). Um try/catch PRÓPRIO, sem relançar: se
            // ficasse dentro do try principal, uma falha aqui (ex.: antivírus segurando o .fbk,
            // disco cheio no HD de backups) cairia no catch de baixo, que roda "gbak -c
            // -replace_database" com o backup PRÉ-atualização -- revertendo silenciosamente uma
            // atualização que já tinha dado certo e já tinha sido confirmada, e ainda reportando
            // "ERRO" à API por cima do "SUCESSO" já enviado.
            try
            {
                ArquivarBackups(sistema, preBkp, posBkp, versaoAlvo);
                if (Directory.Exists(pastaPacotes)) Directory.Delete(pastaPacotes, true);

                // Só agora, com a Fase 3/4 deste sistema com script CONFIRMADAMENTE concluída (não
                // antes, no momento da autorização) -- se tivesse caído no catch abaixo e revertido
                // pelo backup, os sistemas sem script continuariam PENDENTE em vez de ficar numa
                // versão nova com o JUNIOR.fdb de volta na antiga.
                await AplicarPendentesSemScriptAsync(sistema, stoppingToken);
            }
            catch (Exception exPosSucesso)
            {
                _logger.LogWarning(exPosSucesso, "Falha na limpeza pós-sucesso (arquivar backup/limpar pacotes/aplicar sistemas sem script) de {sistema} -- a atualização em si já está confirmada e NÃO foi revertida.", sistema);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha crítica durante atualização do sistema {sistema}.", sistema);
            try
            {
                if (backupValido && File.Exists(preBkp))
                {
                    await _processService.RunProcessAsync(_config.GbakPath, new[] { "-c", "-replace_database", preBkp, alvoJunior }, GbakTimeout, stoppingToken, credenciaisEnv);
                }
                // Testado: depois de "-c -replace_database", o banco resultante já fica acessível
                // sozinho -- esse "-online" aqui frequentemente falha com "Target shutdown mode is
                // invalid" (não há shutdown nenhum pra desfazer), mesmo o banco já estando
                // utilizável. Por isso fica dentro do try/catch: uma falha aqui não indica
                // necessariamente que o banco ficou inacessível, só que não havia shutdown ativo
                // pra desfazer.
                await _processService.RunProcessAsync(_config.GfixPath, new[] { "-online", alvoJunior }, GfixTimeout, stoppingToken, credenciaisEnv);
            }
            catch (Exception onlineError)
            {
                _logger.LogError(onlineError, "gfix -online falhou após a falha original -- pode só significar que o banco já não estava em shutdown (comum após um restore).");
            }

            // Sem revert de versão pra fazer aqui: VERSAO_ATUAL só é avançada em
            // ConfirmarVersaoAtual, no caminho de sucesso -- se caiu aqui, ela nunca mudou.
            _databaseService.SetStatusAtualizacao(_config.JuniorFdbPath, sistema, "ERRO", null, ex.Message);
            // "versaoAlvo" aqui é a versão que esta tentativa buscava e NÃO alcançou (o rollback
            // acima já devolveu o banco pro estado de "versaoAnterior") -- é o que o painel precisa
            // pra mostrar "tentou ir pra 2026.09.01, falhou, continua na 2026.08.27". "faseAtual"
            // ainda vale a etapa onde a exceção aconteceu (nunca foi reatribuída no catch).
            await _apiService.SendLog(_config.CodigoCliente, sistema, "ERRO", ex.Message, versaoAlvo, versaoAnterior, cronometro.Elapsed, stoppingToken, fase: faseAtual);
            return false;
        }
    }

    // Backups pré/pós sobrevivem ao ciclo -- antes, o caminho de sucesso apagava PastaTrabalho
    // inteira (nada lia os .fbk, pareciam lixo), mas são exatamente o que um DBA precisaria pra
    // restaurar manualmente se um problema aparecer dias depois, quando o rollback automático do
    // próprio agente já não se aplica mais. Move pra PastaBackups (fora de PastaTrabalho) com nome
    // único por sistema+versão+timestamp, pra não sobrescrever ciclos anteriores nem de outros
    // sistemas.
    private void ArquivarBackups(string sistema, string preBkp, string posBkp, string versaoAlvo)
    {
        Directory.CreateDirectory(_config.PastaBackups);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string versaoArquivo = new string(versaoAlvo.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        if (File.Exists(preBkp))
            File.Move(preBkp, Path.Combine(_config.PastaBackups, $"JUNIOR_PRE_{sistema}_{versaoArquivo}_{timestamp}.fbk"), overwrite: true);
        if (File.Exists(posBkp))
            File.Move(posBkp, Path.Combine(_config.PastaBackups, $"JUNIOR_POS_{sistema}_{versaoArquivo}_{timestamp}.fbk"), overwrite: true);

        PodarBackupsAntigos();
    }

    // Mantém só os últimos BackupsParaManter ciclos (pré + pós = 2 arquivos por ciclo bem-
    // sucedido) -- sem limpeza, cada atualização deixaria 2 backups novos parados pra sempre, e um
    // JUNIOR.fdb real pode ter centenas de MB/GB por cópia. Poda o total de PastaBackups (não por
    // sistema): já que só sistemas com script (hoje, só um) geram backup, isso hoje equivale a por
    // sistema; se um dia mais de um sistema tiver script, vale revisitar pra podar por sistema.
    private void PodarBackupsAntigos()
    {
        var antigos = Directory.GetFiles(_config.PastaBackups, "*.fbk")
            .OrderByDescending(File.GetCreationTimeUtc)
            .Skip(_config.BackupsParaManter * 2);
        foreach (var arquivo in antigos) File.Delete(arquivo);
    }
}
