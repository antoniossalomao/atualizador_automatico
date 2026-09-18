using AtualizadorERP.Services;
using Xunit;

namespace AtualizadorERP.Tests;

/// <summary>
/// Cobre a validação de startup adicionada na revisão de código de 2026-09-17: GFIX_PATH/
/// GBAK_PATH/ISQL_PATH errados e DB_PORT inválido precisam falhar aqui, na inicialização, em vez
/// de só aparecer no meio de um ciclo real (gfix -shut já rodou, banco em shutdown, aí o gfix/gbak
/// seguinte nem consegue iniciar pra religar).
/// </summary>
public class ConfiguracaoAgenteTests
{
    private static string NovoIni(params string[] linhasExtras)
    {
        string pasta = Directory.CreateTempSubdirectory("atualizador_ini_teste_").FullName;
        string caminho = Path.Combine(pasta, "atualizador.ini");
        var linhas = new List<string>
        {
            "CODIGO_CLIENTE=00000000000000",
            "SISTEMAS=SISTEMA_TESTE:sistema_teste.exe",
            "API_TOKEN=token-de-teste",
            "DB_PASSWORD=senha-de-teste",
        };
        linhas.AddRange(linhasExtras);
        File.WriteAllLines(caminho, linhas);
        return caminho;
    }

    [Fact]
    public void DB_PORT_nao_numerico_lanca_excecao_na_inicializacao()
    {
        string caminhoIni = NovoIni("DB_PORT=nao-e-numero");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => TestAmbiente.NovaConfiguracaoDeArquivo(caminhoIni));
            Assert.Contains("DB_PORT", ex.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(caminhoIni)!, true);
        }
    }

    [Fact]
    public void GFIX_PATH_inexistente_lanca_excecao_na_inicializacao()
    {
        string caminhoIni = NovoIni(@"GFIX_PATH=C:\caminho\que\nao\existe\gfix.exe");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => TestAmbiente.NovaConfiguracaoDeArquivo(caminhoIni));
            Assert.Contains("GFIX_PATH", ex.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(caminhoIni)!, true);
        }
    }

    // Unico teste desta classe que precisa dos binarios do Firebird existindo de verdade: os
    // outros dois exercitam caminhos de ERRO, que falham antes de qualquer checagem de arquivo.
    [Fact]
    [Trait("Requer", "Firebird")]
    public void Ini_com_ferramentas_validas_carrega_sem_lancar()
    {
        string caminhoIni = NovoIni(
            $"GFIX_PATH={TestAmbiente.FirebirdBin}\\gfix.exe",
            $"GBAK_PATH={TestAmbiente.FirebirdBin}\\gbak.exe",
            $"ISQL_PATH={TestAmbiente.FirebirdBin}\\isql.exe",
            "DB_PORT=3050");
        try
        {
            var config = TestAmbiente.NovaConfiguracaoDeArquivo(caminhoIni);
            Assert.Equal("3050", config.DbPort);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(caminhoIni)!, true);
        }
    }
}
