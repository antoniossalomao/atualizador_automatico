namespace AtualizadorERP.Tests;

/// <summary>
/// Variáveis de ambiente que todo serviço do agente lê diretamente (Environment.GetEnvironmentVariable
/// -- não há injeção de configuração neste projeto, ver README.md). Setadas uma vez por processo de
/// teste, apontando para o Firebird 2.5 real instalado nesta máquina (confirmado rodando na porta
/// 3050, SYSDBA/masterkey -- os mesmos usados nos testes manuais registrados em RISCOS-CONHECIDOS.md).
///
/// Só existe porque Worker.cs lê boa parte dessas variáveis em inicializadores de campo, resolvidos
/// na construção do objeto -- setar depois de instanciar um Worker não teria efeito.
/// </summary>
public static class TestAmbiente
{
    private static readonly object Trava = new();
    private static bool _configurado;

    public static void Garantir()
    {
        lock (Trava)
        {
            if (_configurado) return;

            const string firebirdBin = @"C:\Program Files (x86)\Firebird\Firebird_2_5\bin";
            Environment.SetEnvironmentVariable("ATUALIZADOR_GFIX_PATH", $@"{firebirdBin}\gfix.exe");
            Environment.SetEnvironmentVariable("ATUALIZADOR_GBAK_PATH", $@"{firebirdBin}\gbak.exe");
            Environment.SetEnvironmentVariable("ATUALIZADOR_ISQL_PATH", $@"{firebirdBin}\isql.exe");
            Environment.SetEnvironmentVariable("ATUALIZADOR_DB_USER", FirebirdTestDatabase.Usuario);
            Environment.SetEnvironmentVariable("ATUALIZADOR_DB_PASSWORD", FirebirdTestDatabase.Senha);
            Environment.SetEnvironmentVariable("ATUALIZADOR_DB_PORT", "3050");
            Environment.SetEnvironmentVariable("ATUALIZADOR_API_URL", "http://localhost:59999/api-inexistente");
            Environment.SetEnvironmentVariable("ATUALIZADOR_API_TOKEN", "token-de-teste");
            Environment.SetEnvironmentVariable("ATUALIZADOR_CNPJ", "00000000000000");
            Environment.SetEnvironmentVariable("ATUALIZADOR_SISTEMA", "SISTEMA_TESTE");

            _configurado = true;
        }
    }
}
