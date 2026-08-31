using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtualizadorERP.Services;

public class ApiService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _agentToken;

    public ApiService()
    {
        // Sem timeout do HttpClient: o padrão de 100s do .NET matava downloads de pacotes
        // grandes em links de cliente ruins. Quem cancela agora é o CancellationToken passado
        // até aqui a partir do stoppingToken do Worker -- inclusive permite parar o serviço no
        // meio de um download, o que o timeout fixo não permitia.
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _baseUrl = (Environment.GetEnvironmentVariable("ATUALIZADOR_API_URL") ?? "http://localhost:3000/api").TrimEnd('/');
        _agentToken = Environment.GetEnvironmentVariable("ATUALIZADOR_API_TOKEN") ?? "";
        if (string.IsNullOrWhiteSpace(_agentToken))
            throw new InvalidOperationException("Defina ATUALIZADOR_API_TOKEN antes de iniciar o agente.");
    }

    // Propaga qualquer falha (401 de token errado, DNS morto, JSON inválido) em vez de engolir
    // e devolver null: um retorno "sem atualização" tem que significar isso de verdade, não
    // "a checagem quebrou". O catch em Worker.ExecuteAsync já loga a exceção real e incrementa
    // _falhasConsecutivas -- antes disso, uma API fora do ar era indistinguível de um ciclo são,
    // e o cliente ficava invisível sem log local nem backoff.
    public async Task<UpdateResponse?> CheckForUpdates(string cnpj, string versaoAtual)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/update/check/{Uri.EscapeDataString(cnpj)}?versao={Uri.EscapeDataString(versaoAtual)}");
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

    public async Task SendLog(string cnpj, string status, string detalhes)
    {
        try
        {
            var payload = new { cnpj, status, detalhes };
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
    public string Version { get; set; } = string.Empty;
    public List<PackageInfo> Packages { get; set; } = new();
    [JsonPropertyName("script_url")]
    public string? ScriptUrl { get; set; }
}

public class PackageInfo
{
    public string File { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
