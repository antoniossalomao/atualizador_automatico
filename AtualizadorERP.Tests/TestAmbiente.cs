using AtualizadorERP.Services;

namespace AtualizadorERP.Tests;

/// <summary>
/// Configuração compartilhada pelos testes que não precisam de banco/pasta de trabalho próprios
/// (a maioria dos testes de DatabaseService/ScriptRunnerService: os métodos já recebem o caminho
/// do banco por parâmetro, só as credenciais/caminhos de ferramenta do Firebird são fixos).
/// Testes que precisam de JUNIOR/BEXE/pasta de trabalho próprios (WorkerIntegrationTests) usam
/// <see cref="ConfiguracaoAgente"/> com o construtor interno de teste diretamente, com os
/// overrides que precisarem.
/// </summary>
public static class TestAmbiente
{
    public const string FirebirdBin = @"C:\Program Files (x86)\Firebird\Firebird_2_5\bin";

    public static ConfiguracaoAgente Config { get; } = NovaConfiguracao();

    public static ConfiguracaoAgente NovaConfiguracao(
        string? juniorFdbPath = null,
        string? bexeFdbPath = null,
        string? pastaTrabalho = null,
        string? pastaBackups = null,
        int backupsParaManter = 10)
    {
        string trabalho = pastaTrabalho ?? Directory.CreateTempSubdirectory("atualizador_trabalho_").FullName;
        string backups = pastaBackups ?? Directory.CreateTempSubdirectory("atualizador_backups_").FullName;

        return new ConfiguracaoAgente(
            codigoCliente: "00000000000000",
            sistema: "SISTEMA_TESTE",
            apiUrl: "http://localhost:59999/api-inexistente",
            apiToken: "token-de-teste",
            dbUser: FirebirdTestDatabase.Usuario,
            dbPassword: FirebirdTestDatabase.Senha,
            dbPort: "3050",
            juniorFdbPath: juniorFdbPath ?? "",
            bexeFdbPath: bexeFdbPath ?? "",
            gfixPath: $@"{FirebirdBin}\gfix.exe",
            gbakPath: $@"{FirebirdBin}\gbak.exe",
            isqlPath: $@"{FirebirdBin}\isql.exe",
            pastaTrabalho: trabalho,
            pastaBackups: backups,
            backupsParaManter: backupsParaManter);
    }
}
