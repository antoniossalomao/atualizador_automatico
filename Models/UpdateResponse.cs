using System.Text.Json.Serialization;

namespace AtualizadorERP.Models;

/// <summary>
/// Resposta de <c>GET /api/agente/verificar</c>: o que o servidor central respondeu quando o
/// agente perguntou se existe versão nova para um sistema deste cliente.
/// Ver "Contrato da API" no README.
/// </summary>
public class UpdateResponse
{
    /// <summary>
    /// <c>false</c> quando o cliente já está na versão mais recente -- o ciclo termina aqui, sem
    /// baixar nada. Todos os demais campos só têm valor útil quando isto é <c>true</c>.
    /// </summary>
    [JsonPropertyName("update_available")]
    public bool HasUpdate { get; set; }

    // "Sistema" e "Notes" batem com "sistema"/"notes" do JSON por comparação sem diferenciar
    // maiúsculas (PropertyNameCaseInsensitive, configurado no Deserialize do ApiService) -- não
    // precisam de [JsonPropertyName] explícito, diferente de "update_available"/"script_url", que
    // têm sublinhado no JSON e não batem com o PascalCase do C# nem ignorando maiúsculas.

    /// <summary>Nome do sistema ao qual esta resposta se refere (eco do que foi perguntado).</summary>
    public string Sistema { get; set; } = string.Empty;

    /// <summary>Versão de destino -- é ela que vai para <c>SYS_ATUALIZACAO</c> e para o nome dos backups.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Arquivos que compõem o pacote desta versão, cada um com URL e hash para conferência.</summary>
    public List<PackageInfo> Packages { get; set; } = new();

    /// <summary>URL do pacote de scripts SQL, quando a versão traz scripts. <c>null</c> quando não traz.</summary>
    [JsonPropertyName("script_url")]
    public string? ScriptUrl { get; set; }

    /// <summary>Notas da versão, exibidas ao usuário pelo ERP Delphi na hora de autorizar.</summary>
    public string? Notes { get; set; }
}
