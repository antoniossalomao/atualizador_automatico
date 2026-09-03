using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtualizadorERP.Services;

public class ApiService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _agentToken;

    public ApiService(ConfiguracaoAgente config)
    {
        // Sem timeout do HttpClient: o padrão de 100s do .NET matava downloads de pacotes
        // grandes em links de cliente ruins. Quem cancela agora é o CancellationToken passado
        // até aqui a partir do stoppingToken do Worker -- inclusive permite parar o serviço no
        // meio de um download, o que o timeout fixo não permitia.
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
    // SISTEMA em vez de uma só global (ver web/docs/REVISAO_INTERFACE.md, seção "Contrato do
    // agente"). Antes, sem esse parâmetro, o servidor respondia com a última versão publicada de
    // QUALQUER sistema -- um agente cuidando do B_Vendas podia acabar recebendo o pacote do B_NFe.
    // Um agente que cuida de vários sistemas faz uma chamada por sistema.
    public async Task<UpdateResponse?> CheckForUpdates(string cnpj, string sistema, string versaoAtual)
    {
        string query = $"sistema={Uri.EscapeDataString(sistema)}&versao={Uri.EscapeDataString(versaoAtual)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/update/check/{Uri.EscapeDataString(cnpj)}?{query}");
        request.Headers.Add("X-Agent-Token", _agentToken);
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<UpdateResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
            await BaixarArquivoAutenticadoAsync(downloadUrl, filePath, cancellationToken);

            if (!string.IsNullOrWhiteSpace(pkg.Sha256))
            {
                await using var downloaded = File.OpenRead(filePath);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded)).ToLowerInvariant();
                if (!hash.Equals(pkg.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Hash SHA-256 inválido para {fileName}.");
            }
            caminhos.Add(filePath);
        }
        return caminhos;
    }

    private async Task BaixarArquivoAutenticadoAsync(string downloadUrl, string filePath, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.Add("X-Agent-Token", _agentToken);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var fs = new FileStream(filePath, FileMode.Create);
        await response.Content.CopyToAsync(fs, cancellationToken);
    }

    // Nome da máquina lido uma vez só (não muda durante a vida do processo) -- é o que o painel
    // de Distribuição mostra ao lado da empresa (ex.: "Padaria Central · CAIXA-01"), útil quando o
    // mesmo CNPJ tem várias estações rodando o agente.
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
    /// </summary>
    public async Task SendLog(string cnpj, string sistema, string status, string detalhes, string? versao = null, string? versaoAnterior = null, TimeSpan? duracao = null)
    {
        try
        {
            var payload = new
            {
                cnpj,
                sistema,
                status,
                detalhes,
                versao = versao ?? "",
                versaoAnterior = versaoAnterior ?? "",
                duracaoMs = duracao.HasValue ? (long?)Math.Round(duracao.Value.TotalMilliseconds) : null,
                maquina = _nomeMaquina,
            };
            var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/update/log") { Content = content };
            request.Headers.Add("X-Agent-Token", _agentToken);
            await _httpClient.SendAsync(request);
        }
        catch { /* Fire and forget */ }
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
