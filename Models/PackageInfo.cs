using System.Text.Json.Serialization;

namespace AtualizadorERP.Models;

/// <summary>
/// Um arquivo dentro do pacote de uma versão. O <see cref="Sha256"/> é conferido pelo
/// <see cref="Services.ApiService"/> logo após o download: um pacote truncado por queda de rede
/// chegaria "com sucesso" em HTTP e só quebraria lá na frente, no meio da Fase 3, com o banco já
/// em shutdown.
/// </summary>
public class PackageInfo
{
    /// <summary>Nome do arquivo como ele deve ser gravado na pasta de trabalho.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>URL absoluta de download (montada pelo servidor a partir de <c>PUBLIC_URL</c>).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Hash SHA-256 esperado do arquivo, em hexadecimal minúsculo.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
