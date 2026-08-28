namespace AtualizadorERP.Services;

public class ExtractionService
{
    private readonly ILogger<ExtractionService> _logger;
    private readonly ProcessService _processService;

    // Caminho do 7za.exe que deve estar junto com seu serviço
    private readonly string _7zipPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7za.exe");

    public ExtractionService(ILogger<ExtractionService> logger, ProcessService processService)
    {
        _logger = logger;
        _processService = processService;
    }

    /// <summary>
    /// Extrai exatamente os arquivos que acabaram de ser baixados nesta rodada. A lista vem de
    /// quem baixou (a API já diz quais arquivos fazem parte do pacote) em vez de vir de uma
    /// varredura da pasta por extensão -- o 7-Zip lê o cabeçalho binário do arquivo, então não
    /// há motivo para depender da extensão estar certa, e uma varredura por extensão corria o
    /// risco de simplesmente ignorar em silêncio um pacote com nome inesperado.
    /// </summary>
    public async Task ExtractAllAsync(IEnumerable<string> downloadedFiles, string targetDirectory, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_7zipPath))
        {
            throw new FileNotFoundException("7za.exe não encontrado no diretório do agente.", _7zipPath);
        }

        foreach (var arquivo in downloadedFiles)
        {
            _logger.LogInformation("Extraindo: {arquivo}", arquivo);

            // Comando do 7zip: x (extract with full paths) -y (yes to all prompts) -o (output dir)
            await _processService.RunProcessAsync(_7zipPath, new[] { "x", arquivo, $"-o{targetDirectory}", "-y" }, TimeSpan.FromMinutes(5), cancellationToken);

            // Opcional: deletar o arquivo compactado após extrair
            File.Delete(arquivo);
        }
    }
}
