using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtualizadorERP.Services;

public class ApiService
{
    private readonly ILogger<ApiService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _agentToken;

    // Timeout curto só para checagem de versão e envio de log -- chamadas pequenas por natureza
    // (um GET/POST comum, não um download). Sem isso, uma conexão travada (nem erro nem sucesso,
    // uma chamada que simplesmente nunca retorna) prendia o ciclo inteiro até alguém parar o
    // serviço na mão: o backoff nunca entrava em ação porque a chamada nunca terminava, com ou
    // sem erro. O download de pacote continua com o timeout infinito do _httpClient (ver
    // construtor) -- só estas duas chamadas ganham um teto próprio, via
    // CancellationTokenSource.CreateLinkedTokenSource combinado ao stoppingToken do Worker (para
    // parar o serviço no meio de uma checagem continuar funcionando, igual já funcionava para
    // download).
    private static readonly TimeSpan TimeoutChecagemELog = TimeSpan.FromSeconds(30);

    // Retry curto só para quedas de rede NO MEIO de um download -- não serve para erro de
    // autenticação (401/403), que não se resolve tentando de novo (ver
    // DeveTentarNovamenteAposFalha). Motivado por um teste real (03/09/2026): um pacote de 50MB
    // caiu perto do fim, e a próxima tentativa só rodava na próxima janela do backoff do Worker
    // (até 30min), recomeçando o download do zero.
    private const int TentativasDownload = 3;
    private static readonly TimeSpan[] AtrasosRetryDownload = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };

    public ApiService(ILogger<ApiService> logger, ConfiguracaoAgente config)
    {
        _logger = logger;
        // Sem timeout do HttpClient: o padrão de 100s do .NET matava downloads de pacotes
        // grandes em links de cliente ruins. Quem cancela agora é o CancellationToken passado
        // até aqui a partir do stoppingToken do Worker -- inclusive permite parar o serviço no
        // meio de um download, o que o timeout fixo não permitia. Checagem de versão e envio de
        // log usam o teto próprio e curto de TimeoutChecagemELog, não este timeout infinito.
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _baseUrl = config.ApiUrl.TrimEnd('/');
        _agentToken = config.ApiToken;
    }

    // Propaga qualquer falha (401 de token errado, DNS morto, JSON inválido) em vez de engolir
    // e devolver null: um retorno "sem atualização" tem que significar isso de verdade, não
    // "a checagem quebrou". O catch em Worker.ExecuteAsync já loga a exceção real e incrementa
    // _falhasConsecutivas -- antes disso, uma API fora do ar era indistinguível de um ciclo são,
    // e o cliente ficava invisível sem log local nem backoff.
    //
    // "sistema" é obrigatório desde que o servidor passou a manter uma versão publicada POR
    // SISTEMA em vez de uma só global (ver web/docs/DOCUMENTACAO_CONSOLIDADA.md, seção "Contrato
    // do agente (Worker C#)"). Antes, sem esse parâmetro, o servidor respondia com a última versão publicada de
    // QUALQUER sistema -- um agente cuidando do B_Vendas podia acabar recebendo o pacote do B_NFe.
    // Um agente que cuida de vários sistemas faz uma chamada por sistema.
    public async Task<UpdateResponse?> CheckForUpdates(string codigoCliente, string sistema, string versaoAtual, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Verificando atualização do sistema {Sistema} para o cliente {Cliente} (versão atual: {VersaoAtual}).", sistema, codigoCliente, versaoAtual);

        // A API continua recebendo isso no parametro de URL "cnpj" (contrato do servidor, ver
        // web/server/src/routes) -- só o nome do lado do agente mudou pra refletir o que o valor
        // realmente é na prática (o "codigo" do cliente cadastrado no painel, não uma CNPJ de
        // verdade; ver README.md e RISCOS-CONHECIDOS.md).
        string query = $"sistema={Uri.EscapeDataString(sistema)}&versao={Uri.EscapeDataString(versaoAtual)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/update/check/{Uri.EscapeDataString(codigoCliente)}?{query}");
        request.Headers.Add("X-Agent-Token", _agentToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeoutChecagemELog);
        var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        await GarantirSucessoComCorpoAsync(response);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var resultado = JsonSerializer.Deserialize<UpdateResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        _logger.LogInformation(
            "Sistema {Sistema}: {Resultado}.",
            sistema,
            resultado?.HasUpdate == true ? $"atualização disponível (versão {resultado.Version})" : "já está na versão mais recente"
        );
        return resultado;
    }

    /// <summary>
    /// Confere o status da resposta e, em caso de falha, inclui o CORPO da resposta na mensagem
    /// da exceção -- <c>EnsureSuccessStatusCode()</c> descarta esse corpo, então um 401 com
    /// <c>{"error":"token inválido"}</c> virava só "401 Unauthorized" no log, sem o motivo real.
    /// Preserva o <see cref="HttpStatusCode"/> na exceção (não só no texto), para quem capturar
    /// poder distinguir "não adianta tentar de novo" (401/403) de uma falha transitória -- ver
    /// <see cref="DeveTentarNovamenteAposFalha"/>.
    /// </summary>
    private static async Task GarantirSucessoComCorpoAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string corpo = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"Erro {(int)response.StatusCode} ({response.StatusCode}) da API: {(string.IsNullOrWhiteSpace(corpo) ? "(sem corpo na resposta)" : corpo)}",
            inner: null,
            statusCode: response.StatusCode
        );
    }

    /// <summary>Baixa os pacotes de uma versão, valida o SHA-256 de cada um e devolve os
    /// caminhos locais -- usados depois para saber exatamente o que extrair, sem precisar
    /// adivinhar por extensão de arquivo.</summary>
    public async Task<List<string>> DownloadPackages(List<PackageInfo> packages, string destinationPath, CancellationToken cancellationToken = default)
    {
        var caminhos = new List<string>();
        foreach (var pkg in packages)
        {
            string fileName = Path.GetFileName(pkg.File);
            if (string.IsNullOrWhiteSpace(fileName) || fileName != pkg.File)
                throw new InvalidOperationException($"Nome de pacote inválido: {pkg.File}");
            string filePath = Path.Combine(destinationPath, fileName);
            string downloadUrl = Uri.TryCreate(pkg.Url, UriKind.Absolute, out _) ? pkg.Url : $"{_baseUrl}/{pkg.Url.TrimStart('/')}";
            _logger.LogInformation("Baixando pacote {Arquivo}.", fileName);
            await BaixarArquivoAutenticadoAsync(downloadUrl, filePath, cancellationToken);

            if (!string.IsNullOrWhiteSpace(pkg.Sha256))
            {
                await using var downloaded = File.OpenRead(filePath);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded)).ToLowerInvariant();
                if (!hash.Equals(pkg.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Hash SHA-256 inválido para {fileName}.");
            }
            _logger.LogInformation("Pacote {Arquivo} baixado e com hash conferido ({Bytes} bytes).", fileName, new FileInfo(filePath).Length);
            caminhos.Add(filePath);
        }
        return caminhos;
    }

    private async Task BaixarArquivoAutenticadoAsync(string downloadUrl, string filePath, CancellationToken cancellationToken = default)
    {
        for (int tentativa = 1; ; tentativa++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                request.Headers.Add("X-Agent-Token", _agentToken);
                var response = await _httpClient.SendAsync(request, cancellationToken);
                await GarantirSucessoComCorpoAsync(response);

                using var fs = new FileStream(filePath, FileMode.Create);
                await response.Content.CopyToAsync(fs, cancellationToken);
                return;
            }
            catch (Exception ex) when (tentativa < TentativasDownload && DeveTentarNovamenteAposFalha(ex, cancellationToken))
            {
                TimeSpan atraso = AtrasosRetryDownload[Math.Min(tentativa, AtrasosRetryDownload.Length) - 1];
                _logger.LogWarning(ex, "Falha ao baixar {Arquivo} (tentativa {Tentativa}/{Total}). Tentando de novo em {Atraso}.", Path.GetFileName(filePath), tentativa, TentativasDownload, atraso);
                await Task.Delay(atraso, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Decide se vale a pena tentar o download de novo. Erro de autenticação (401/403) não se
    /// resolve tentando de novo -- é um problema de configuração (token errado/expirado), não uma
    /// falha de rede transitória, e insistir só atrasaria o backoff de verdade que o Worker
    /// precisa aplicar. Cancelamento explícito (serviço sendo parado) também não deve virar
    /// retry -- ver o `cancellationToken.ThrowIfCancellationRequested` implícito no
    /// `Task.Delay`/`SendAsync` acima, que já propaga `OperationCanceledException` nesse caso.
    /// </summary>
    private static bool DeveTentarNovamenteAposFalha(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (ex is OperationCanceledException) return false;
        if (ex is HttpRequestException http && (http.StatusCode == System.Net.HttpStatusCode.Unauthorized || http.StatusCode == System.Net.HttpStatusCode.Forbidden))
            return false;
        return true;
    }

    // Nome da máquina lido uma vez só (não muda durante a vida do processo) -- é o que o painel
    // de Distribuição mostra ao lado da empresa (ex.: "Padaria Central · CAIXA-01"), útil quando o
    // mesmo código de cliente tem várias estações rodando o agente.
    private static readonly string _nomeMaquina = Environment.MachineName;

    /// <summary>
    /// Reporta o resultado de uma execução à API central. "Fire and forget": uma falha aqui
    /// (rede fora, API fora do ar) não pode derrubar o ciclo de atualização que já rodou -- o
    /// agente já fez seu trabalho local (ScriptRunnerService.RunPendingScriptsAsync já tentou
    /// registrar o script) independente de o painel ficar sabendo na hora.
    ///
    /// <paramref name="sistema"/> identifica de qual sistema é este retorno -- sem ele, o painel
    /// de Distribuição não consegue comparar "versão instalada" contra "versão publicada" (ver
    /// VersaoService.painel() no servidor), e o agente aparece como "em andamento" para sempre.
    /// <paramref name="versao"/>/<paramref name="versaoAnterior"/>/<paramref name="duracao"/> são
    /// opcionais -- ficam nulos nos logs de falha de script individual (ScriptRunnerService), que
    /// reportam um problema no MEIO do processo, não a transição de versão completa.
    /// <paramref name="fase"/> identifica EM QUE ETAPA da Fase 3 isto aconteceu (shutdown,
    /// backup_pre, scripts, copia_arquivos, injecao_binarios, online, backup_pos, concluido,
    /// aguardando_autorizacao) -- sem isso, um ERRO só trazia a mensagem crua da exceção, nunca
    /// onde no processo ela aconteceu (ver Worker.ProcessarAtualizacao, que rastreia isso num
    /// "faseAtual" local e reporta o valor de quando quebrou, não de onde deveria ter chegado).
    /// </summary>
    public async Task SendLog(string codigoCliente, string sistema, string status, string detalhes, string? versao = null, string? versaoAnterior = null, TimeSpan? duracao = null, CancellationToken cancellationToken = default, string? fase = null)
    {
        try
        {
            // "cnpj" no payload, não "codigoCliente": é o nome de campo que o endpoint
            // /update/log do servidor espera (ver web/server/src/services/VersaoService.js) --
            // contrato de rede não muda só porque o nome do lado do agente mudou.
            var payload = new
            {
                cnpj = codigoCliente,
                sistema,
                status,
                detalhes,
                versao = versao ?? "",
                versaoAnterior = versaoAnterior ?? "",
                duracaoMs = duracao.HasValue ? (long?)Math.Round(duracao.Value.TotalMilliseconds) : null,
                fase = fase ?? "",
                maquina = _nomeMaquina,
            };
            var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/update/log") { Content = content };
            request.Headers.Add("X-Agent-Token", _agentToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeoutChecagemELog);
            var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            await GarantirSucessoComCorpoAsync(response);
        }
        catch (Exception ex)
        {
            // Fire and forget continua correto aqui -- reportar o resultado ao painel não pode
            // derrubar um ciclo de atualização que já rodou de verdade localmente (o trabalho já
            // foi feito; só o AVISO ao servidor central que falhou). O que mudou é o silêncio
            // total: antes um `catch {}` vazio não deixava rastro nenhum, nem local -- um cliente
            // com a API inacessível no momento do log tinha a atualização real registrada só no
            // banco dele, sem nenhum sinal de que o painel central nunca soube.
            _logger.LogWarning(ex, "Falha ao enviar log para a API (cliente {Cliente}, sistema {Sistema}, status {Status}).", codigoCliente, sistema, status);
        }
    }
}

public class UpdateResponse
{
    [JsonPropertyName("update_available")]
    public bool HasUpdate { get; set; }
    // "Sistema" e "Notes" batem com "sistema"/"notes" do JSON por comparação sem diferenciar
    // maiúsculas (PropertyNameCaseInsensitive, configurado no Deserialize acima) -- não precisam
    // de [JsonPropertyName] explícito, diferente de "update_available"/"script_url", que têm
    // sublinhado no JSON e não batem com o PascalCase do C# nem ignorando maiúsculas.
    public string Sistema { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<PackageInfo> Packages { get; set; } = new();
    [JsonPropertyName("script_url")]
    public string? ScriptUrl { get; set; }
    public string? Notes { get; set; }
}

public class PackageInfo
{
    public string File { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
