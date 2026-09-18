namespace AtualizadorERP.Models;

/// <summary>
/// Um sistema que este agente pode cuidar, e o nome do executável que identifica se o cliente
/// atual realmente tem esse sistema instalado (ex.: Nome="B_NFe", NomeExeEsperado="B_NFE.exe" --
/// o nome do sistema no painel e o nome do arquivo real não batem sempre, então não dá pra
/// inferir um a partir do outro).
/// </summary>
/// <remarks>
/// É um <c>record</c> (e não uma classe comum) de propósito: a configuração é lida uma vez, na
/// inicialização, e nunca mais muda -- igualdade por valor e imutabilidade evitam que qualquer
/// ponto do ciclo altere por engano a lista de sistemas que o <c>Worker</c>
/// está iterando.
/// </remarks>
public sealed record SistemaConfigurado(string Nome, string NomeExeEsperado);
