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

    private readonly string _cnpjCliente = Environment.GetEnvironmentVariable("ATUALIZADOR_CNPJ") ?? "";
    private readonly string _tempPath = Environment.GetEnvironmentVariable("ATUALIZADOR_TEMP_PATH") ?? @"C:\TempUpdates";
    private readonly string _juniorFdbPath = Environment.GetEnvironmentVariable("ATUALIZADOR_JUNIOR_FDB") ?? @"C:\ERP\JUNIOR.fdb";
    private readonly string _bexeFdbPath = Environment.GetEnvironmentVariable("ATUALIZADOR_BEXE_FDB") ?? @"C:\ERP\BEXE.fdb";
    private readonly string _gfixPath = Environment.GetEnvironmentVariable("ATUALIZADOR_GFIX_PATH") ?? @"C:\Program Files (x86)\Firebird\Firebird_2_5\bin\gfix.exe";
    private readonly string _gbakPath = Environment.GetEnvironmentVariable("ATUALIZADOR_GBAK_PATH") ?? @"C:\Program Files (x86)\Firebird\Firebird_2_5\bin\gbak.exe";
    private readonly string _bscriptPath = Environment.GetEnvironmentVariable("ATUALIZADOR_BSCRIPT_PATH") ?? @"C:\ERP\BScript.exe";
    private readonly string _dbUser = Environment.GetEnvironmentVariable("ATUALIZADOR_DB_USER") ?? "SYSDBA";
    private readonly string _dbPassword = Environment.GetEnvironmentVariable("ATUALIZADOR_DB_PASSWORD") ?? "";

    // Não há confirmação de que o BScript.exe real aceite rodar de forma verdadeiramente
    // não-interativa -- por isso ele roda sob um teto de tempo. Se travar numa janela que
    // ninguém pode ver (serviço Windows não tem desktop interativo), o processo é morto e a
    // atualização cai em ERRO com rollback, em vez de deixar o banco em shutdown para sempre.
    private static readonly TimeSpan BScriptTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan GfixTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GbakTimeout = TimeSpan.FromMinutes(15);

    // Guarda, fora do JUNIOR.fdb, qual era a última versão confirmada antes de começar a
    // tentar a atual. Sem isso, uma falha na Fase 3 não tem para qual valor reverter o
    // VERSAO_NOVA -- e o próximo polling passa a enxergar o cliente como "em dia" mesmo a
    // atualização nunca tendo sido aplicada de fato.
    private readonly string _versaoAnteriorFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "versao_anterior.txt");

    private int _falhasConsecutivas = 0;

    public Worker(ILogger<Worker> logger, ApiService apiService, DatabaseService databaseService, ExtractionService extractionService, ProcessService processService)
    {
        _logger = logger;
        _apiService = apiService;
        _databaseService = databaseService;
        _extractionService = extractionService;
        _processService = processService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var statusAtual = _databaseService.GetStatusAtualizacao(_juniorFdbPath);
                if (statusAtual == "CONCLUIDO" || statusAtual == "ERRO")
                {
                    if (string.IsNullOrWhiteSpace(_cnpjCliente))
                        throw new InvalidOperationException("Defina ATUALIZADOR_CNPJ antes de iniciar o agente.");

                    string versaoAtual = _databaseService.GetVersaoAtual(_juniorFdbPath);
                    var updateInfo = await _apiService.CheckForUpdates(_cnpjCliente, versaoAtual);
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
                        var baixados = await _apiService.DownloadPackages(updateInfo.Packages, PastaPacotes);
                        await _extractionService.ExtractAllAsync(baixados, PastaPacotes, stoppingToken);

                        // O contrato da API já previa distribuir o próprio BScript.exe via
                        // "script_url" -- baixa aqui, na Fase 1, para a Fase 3 não depender de
                        // rede durante a janela crítica em que o banco já está isolado.
                        if (!string.IsNullOrWhiteSpace(updateInfo.ScriptUrl))
                        {
                            await _apiService.DownloadFileAsync(updateInfo.ScriptUrl, CaminhoBScriptBaixado, stoppingToken);
                        }

                        await File.WriteAllTextAsync(_versaoAnteriorFile, versaoAtual, stoppingToken);
                        _databaseService.SetStatusAtualizacao(_juniorFdbPath, "PENDENTE", updateInfo.Version);
                    }

                    _falhasConsecutivas = 0; // chegou até aqui sem exceção: ciclo saudável
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

    // Só o conteúdo dos pacotes da versão fica aqui -- e é exatamente esta pasta
    // que a Fase 4 varre atrás de "*.exe" para injetar no BEXE.fdb. Ferramentas
    // do próprio agente (o BScript baixado) e os backups do gbak ficam de fora,
    // na raiz de _tempPath.
    //
    // Sem essa separação, o BScript_atual.exe baixado via "script_url" caía na
    // mesma varredura e era gravado na tabela EXECUTAVEIS como se fosse um
    // executável do ERP -- os terminais o baixariam achando que era atualização
    // deles. A varredura não pode ser substituída por uma lista em memória
    // porque Fase 1 e Fase 4 acontecem em ciclos diferentes (podendo ter um
    // reinício do serviço no meio), então quem separa é o layout de pastas.
    private string PastaPacotes => Path.Combine(_tempPath, "pacotes");

    private string CaminhoBScriptBaixado => Path.Combine(_tempPath, "BScript_atual.exe");

    // Backoff simples: 10s no caminho saudável; cresce até 30 minutos em falhas seguidas, para
    // não martelar disco/rede/API a cada 10 segundos quando algo está persistentemente quebrado
    // (ex.: disco cheio, permissão negada, credencial errada).
    private TimeSpan ProximoIntervalo()
    {
        if (_falhasConsecutivas <= 0) return TimeSpan.FromSeconds(10);
        double minutos = Math.Min(30, Math.Pow(2, _falhasConsecutivas - 1));
        return TimeSpan.FromMinutes(minutos);
    }

    private async Task ProcessarAtualizacao(CancellationToken stoppingToken)
    {
        string preBkp = Path.Combine(_tempPath, "JUNIOR_PRE.fbk");
        if (string.IsNullOrWhiteSpace(_dbPassword))
            throw new InvalidOperationException("Defina ATUALIZADOR_DB_PASSWORD antes de atualizar o banco.");
        string[] credenciais = { "-user", _dbUser, "-password", _dbPassword };

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
            await _processService.RunProcessAsync(_gfixPath, credenciais.Concat(new[] { "-shut", "force_0", _juniorFdbPath }).ToArray(), GfixTimeout, stoppingToken);

            await _processService.RunProcessAsync(_gbakPath, new[] { "-b" }.Concat(credenciais).Concat(new[] { _juniorFdbPath, preBkp }).ToArray(), GbakTimeout, stoppingToken);
            backupValido = true;

            string scriptParaRodar = File.Exists(CaminhoBScriptBaixado) ? CaminhoBScriptBaixado : _bscriptPath;
            await _processService.RunProcessAsync(scriptParaRodar, new[] { "/silent", $"/db={_juniorFdbPath}" }, BScriptTimeout, stoppingToken);

            string posBkp = Path.Combine(_tempPath, "JUNIOR_POS.fbk");
            await _processService.RunProcessAsync(_gbakPath, new[] { "-b" }.Concat(credenciais).Concat(new[] { _juniorFdbPath, posBkp }).ToArray(), GbakTimeout, stoppingToken);

            string versaoNova = _databaseService.GetVersaoAtual(_juniorFdbPath);
            _databaseService.InjetarNovosBinarios(_bexeFdbPath, PastaPacotes, versaoNova);
            await _processService.RunProcessAsync(_gfixPath, credenciais.Concat(new[] { "-online", _juniorFdbPath }).ToArray(), GfixTimeout, stoppingToken);

            _databaseService.SetStatusAtualizacao(_juniorFdbPath, "CONCLUIDO", null);
            await _apiService.SendLog(_cnpjCliente, "SUCESSO", "Atualização concluída com sucesso.");
            if (Directory.Exists(_tempPath)) Directory.Delete(_tempPath, true);
            if (File.Exists(_versaoAnteriorFile)) File.Delete(_versaoAnteriorFile);
            _falhasConsecutivas = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha crítica durante atualização.");
            try
            {
                if (backupValido && File.Exists(preBkp))
                {
                    await _processService.RunProcessAsync(_gbakPath, new[] { "-c", "-replace_database" }.Concat(credenciais).Concat(new[] { preBkp, _juniorFdbPath }).ToArray(), GbakTimeout, stoppingToken);
                }
                await _processService.RunProcessAsync(_gfixPath, credenciais.Concat(new[] { "-online", _juniorFdbPath }).ToArray(), GfixTimeout, stoppingToken);
            }
            catch (Exception onlineError)
            {
                _logger.LogError(onlineError, "Não foi possível colocar o banco online após a falha.");
            }

            // Reverte VERSAO_NOVA para a última versão confirmada antes desta tentativa. Sem
            // isso, o próximo polling compararia a versão publicada com o valor que ficou
            // gravado na Fase 1 (a versão-alvo que falhou) e concluiria, errado, que o cliente
            // já está em dia -- parando de tentar para sempre.
            string? versaoParaReverter = File.Exists(_versaoAnteriorFile) ? await File.ReadAllTextAsync(_versaoAnteriorFile, stoppingToken) : null;
            _databaseService.SetStatusAtualizacao(_juniorFdbPath, "ERRO", versaoParaReverter, ex.Message);
            await _apiService.SendLog(_cnpjCliente, "ERRO", ex.Message);
            _falhasConsecutivas++;
        }
    }
}
