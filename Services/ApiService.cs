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
        _httpClient = new HttpClient();
        _baseUrl = (Environment.GetEnvironmentVariable("ATUALIZADOR_API_URL") ?? "http://localhost:3000/api").TrimEnd('/');
        _agentToken = Environment.GetEnvironmentVariable("ATUALIZADOR_API_TOKEN") ?? "";
        if (string.IsNullOrWhiteSpace(_agentToken))
            throw new InvalidOperationException("Defina ATUALIZADOR_API_TOKEN antes de iniciar o agente.");
    }

    public async Task<UpdateResponse?> CheckForUpdates(string cnpj, string versaoAtual)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/update/check/{Uri.EscapeDataString(cnpj)}?versao={Uri.EscapeDataString(versaoAtual)}");
            request.Headers.Add("X-Agent-Token", _agentToken);
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<UpdateResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch (Exception)
        {
            // Ignorar falhas de rede no polling
        }
        return null;
    }

    /// <summary>Baixa os pacotes de uma versão, valida o SHA-256 de cada um e devolve os
    /// caminhos locais -- usados depois para saber exatamente o que extrair, sem precisar
    /// adivinhar por extensão de arquivo.</summary>
    public async Task<List<string>> DownloadPackages(List<PackageInfo> packages, string destinationPath)
    {
        var caminhos = new List<string>();
        foreach (var pkg in packages)
        {
            string fileName = Path.GetFileName(pkg.File);
            if (string.IsNullOrWhiteSpace(fileName) || fileName != pkg.File)
                throw new InvalidOperationException($"Nome de pacote inválido: {pkg.File}");
            string filePath = Path.Combine(destinationPath, fileName);
            string downloadUrl = Uri.TryCreate(pkg.Url, UriKind.Absolute, out _) ? pkg.Url : $"{_baseUrl}/{pkg.Url.TrimStart('/')}";
            await BaixarArquivoAutenticadoAsync(downloadUrl, filePath);

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

    /// <summary>
    /// Baixa um arquivo avulso autenticado com o token do agente. Existe porque o contrato da
    /// API já previa um "script_url" por versão publicada, mas o agente nunca chegava a lê-lo
    /// nem baixá-lo -- o BScript.exe usado era sempre o que já estava fixo no servidor do
    /// cliente, exigindo cópia manual sempre que o próprio script precisasse mudar.
    /// </summary>
    public Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
    {
        string downloadUrl = Uri.TryCreate(url, UriKind.Absolute, out _) ? url : $"{_baseUrl}/{url.TrimStart('/')}";
        return BaixarArquivoAutenticadoAsync(downloadUrl, destinationPath, cancellationToken);
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
