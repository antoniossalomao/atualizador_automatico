namespace AtualizadorERP.Models;

/// <summary>
/// Resposta de <c>GET /api/agente/status</c>: a chave geral que o painel web tem para segurar
/// todos os agentes de um cliente sem precisar desinstalar nada. Enquanto
/// <see cref="Pausado"/> for <c>true</c>, o <c>Worker</c> continua vivo e
/// respondendo, mas não inicia ciclo nenhum.
/// </summary>
public class AgentStatusResponse
{
    /// <summary>
    /// <c>true</c> = o agente deve ficar ocioso. Na dúvida (API fora do ar, resposta inválida) o
    /// agente assume <c>false</c> e segue trabalhando -- ver <see cref="Services.ApiService"/>.
    /// </summary>
    public bool Pausado { get; set; }
}
