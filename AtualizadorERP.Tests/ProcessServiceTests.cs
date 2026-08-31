using AtualizadorERP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AtualizadorERP.Tests;

/// <summary>
/// ProcessService é quem garante que a Fase 3 nunca fica travada com o banco em shutdown
/// esperando uma janela interativa (ver item 1 do RISCOS-CONHECIDOS.md) e que credenciais não
/// vazam na linha de comando (item 10). Os dois comportamentos são testados de ponta a ponta
/// contra processos reais, não mockados.
/// </summary>
public class ProcessServiceTests
{
    private readonly ProcessService _processService = new(NullLogger<ProcessService>.Instance);

    [Fact]
    public async Task Processo_que_nao_termina_e_encerrado_a_forca_no_timeout()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            _processService.RunProcessAsync(
                "powershell.exe",
                new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 10" },
                TimeSpan.FromSeconds(2)));

        Assert.Contains("não terminou", ex.Message);
    }

    [Fact]
    public async Task Processo_com_exit_code_diferente_de_zero_lanca_com_stderr_no_texto()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _processService.RunProcessAsync(
                "cmd.exe",
                new[] { "/c", "echo falha-esperada 1>&2 & exit /b 3" },
                TimeSpan.FromSeconds(10)));

        Assert.Contains("ExitCode: 3", ex.Message);
        Assert.Contains("falha-esperada", ex.Message);
    }

    [Fact]
    public async Task Variaveis_de_ambiente_chegam_ao_processo_filho_sem_passar_pela_linha_de_comando()
    {
        // Credencial fake só pra confirmar que o processo filho enxerga a variável de ambiente --
        // não aparece em nenhum argumento (nem no comando abaixo), então se ela aparecer na saída
        // é porque veio do Environment do processo, exatamente como gfix/gbak/isql recebem
        // ISC_USER/ISC_PASSWORD hoje (item 10 do RISCOS-CONHECIDOS.md).
        var env = new Dictionary<string, string> { ["ATUALIZADOR_TESTE_ENV"] = "valor-secreto-de-teste" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _processService.RunProcessAsync(
                "cmd.exe",
                new[] { "/c", "echo %ATUALIZADOR_TESTE_ENV% 1>&2 & exit /b 1" },
                TimeSpan.FromSeconds(10),
                CancellationToken.None,
                env));

        Assert.Contains("valor-secreto-de-teste", ex.Message);
    }

    [Fact]
    public async Task Processo_que_termina_com_sucesso_nao_lanca()
    {
        await _processService.RunProcessAsync("cmd.exe", new[] { "/c", "exit /b 0" }, TimeSpan.FromSeconds(10));
    }
}
