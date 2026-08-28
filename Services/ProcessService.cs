using System.Diagnostics;

namespace AtualizadorERP.Services;

public class ProcessService
{
    private readonly ILogger<ProcessService> _logger;

    public ProcessService(ILogger<ProcessService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Executa um processo externo (gfix, gbak, BScript, 7za...). Os argumentos vão em
    /// ArgumentList, nunca numa única string formatada -- uma senha do Firebird com espaço
    /// ou aspas quebraria o parsing se fosse montada por interpolação.
    ///
    /// O timeout não é opcional por acaso: não há confirmação de que o BScript.exe real aceite
    /// ser chamado de forma verdadeiramente não-interativa. Se ele abrir uma janela dentro do
    /// serviço Windows (sessão sem desktop interativo), sem timeout o processo nunca retorna e
    /// o banco fica em "-shut force_0" (bloqueado para todo mundo) até alguém notar e matar o
    /// processo manualmente no servidor do cliente.
    /// </summary>
    public async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Executando ferramenta: {file}", fileName);

        var processInfo = new ProcessStartInfo
        {
            FileName = fileName,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments) processInfo.ArgumentList.Add(arg);

        using var process = Process.Start(processInfo) ?? throw new InvalidOperationException($"Não foi possível iniciar {fileName}.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process, fileName);
            throw new TimeoutException($"Processo {fileName} não terminou em {timeout} e foi encerrado à força -- provável janela travada esperando interação humana.");
        }

        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Processo {fileName} falhou. ExitCode: {process.ExitCode}. Erro: {error.Trim()} Saída: {output.Trim()}");
    }

    private void TryKill(Process process, string fileName)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { _logger.LogError(ex, "Falha ao encerrar {file} travado.", fileName); }
    }
}
