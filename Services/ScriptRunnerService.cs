namespace AtualizadorERP.Services;

/// <summary>
/// Substitui o BScript.exe na Fase 3. Testado contra uma cópia real do JUNIOR.fdb de um cliente
/// (366 tabelas, 2375 scripts já registrados): "/silent" no BScript.exe é ignorado -- ele sempre
/// abre a tela do Delphi e espera clique, mesmo com a base de dados correta e a tabela SCRIPTS
/// populada. Rodando cada .sql num processo `isql` isolado em vez de chamar o BScript, o ciclo
/// completo (aplicar script -> registrar em SCRIPTS) funciona sem nenhuma tela.
///
/// Reaproveita a própria tabela SCRIPTS que o BScript.exe já mantém (ID, NOME_ARQUIVO,
/// TIPO_EXECUCAO, DATA_EXECUCAO) -- assim o histórico manual que já existe nos clientes reais
/// continua valendo, e o agente nunca reaplica um script que uma pessoa já rodou pela tela.
/// </summary>
public class ScriptRunnerService
{
    private readonly ILogger<ScriptRunnerService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly ProcessService _processService;
    private readonly ApiService _apiService;
    private readonly ConfiguracaoAgente _config;

    // Cada script roda isolado: um .sql com corpo de trigger/procedure sem o próprio "SET TERM"
    // pode confundir o parser do isql, mas isso fica contido a ESSE processo -- não contamina os
    // demais, ao contrário de encadear vários arquivos numa sessão isql só (testado: derruba o
    // lote inteiro no meio).
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(2);

    public ScriptRunnerService(ILogger<ScriptRunnerService> logger, DatabaseService databaseService, ProcessService processService, ApiService apiService, ConfiguracaoAgente config)
    {
        _logger = logger;
        _databaseService = databaseService;
        _processService = processService;
        _apiService = apiService;
        _config = config;
    }

    /// <summary>
    /// Aplica, em ordem alfabética de nome de arquivo, todo ".sql" encontrado em qualquer
    /// subpasta de <paramref name="pacotesPath"/> que ainda não esteja em SYS_ATUALIZACAO/SCRIPTS.
    ///
    /// Antes de rodar um script não registrado, confere nas tabelas de sistema do Firebird se o
    /// objeto que ele cria já existe -- cobre os scripts antigos que foram aplicados décadas atrás,
    /// antes de existir controle na SCRIPTS (confirmado contra um banco real: script de 2005 pro
    /// domain MEMOTEXTO, nunca registrado, mas o domain já existia). Se já existe, marca como
    /// aplicado sem tentar rodar -- não é erro, é sincronizar o controle com a realidade do banco.
    ///
    /// Um script cujo objeto não reconhecemos ou não existia ainda, e mesmo assim o isql retornou
    /// erro, é uma falha de verdade -- mas não interrompe o lote. É reportada pra API na hora
    /// (SendLog "ERRO", com um relatório detalhado, não só a mensagem crua do isql) e o script
    /// fica sem registrar em SCRIPTS (então uma próxima rodada tenta de novo, útil se o motivo for
    /// corrigido manualmente nesse meio-tempo). O lote continua pros scripts seguintes: parar tudo
    /// por causa de um script legado sem tabela/nome batendo (ex.: EMPRESA vs EMPRESAS) bloquearia
    /// pra sempre os milhares de outros que aplicam limpo.
    /// </summary>
    public async Task<int> RunPendingScriptsAsync(string dbPath, string pacotesPath, string cnpjCliente, string sistema, CancellationToken cancellationToken = default)
    {
        var scripts = Directory.GetFiles(pacotesPath, "*.sql", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (scripts.Count == 0)
        {
            _logger.LogInformation("Nenhum script .sql no pacote desta versão.");
            return 0;
        }

        var jaAplicados = _databaseService.GetScriptsAplicados(dbPath);
        int jaAplicadosAntes = jaAplicados.Count;
        _logger.LogInformation("{total} scripts no pacote, {aplicados} já aplicados anteriormente.", scripts.Count, jaAplicadosAntes);

        int posicao = 0;
        int falhas = 0;
        foreach (var scriptPath in scripts)
        {
            posicao++;
            string nomeArquivo = Path.GetFileName(scriptPath);
            // Caminho relativo (ex.: "Scripts-BVendas\scripts2012\X.sql"), não só o nome do
            // arquivo: achamos 26 nomes duplicados entre subpastas diferentes de um pacote real
            // (ex. um "Cria_campo_X.sql" solto na raiz E dentro de "scripts2012"). Registrando só
            // pelo nome, aplicar um marcaria o outro como "já aplicado" pra sempre, sem nunca
            // rodar. Ainda checa o nome puro também, pra continuar batendo com entradas antigas
            // que o BScript.exe gravou manualmente (que só conhecem o nome, não a subpasta).
            string caminhoRelativo = Path.GetRelativePath(pacotesPath, scriptPath);
            if (jaAplicados.Contains(nomeArquivo) || jaAplicados.Contains(caminhoRelativo))
            {
                _logger.LogInformation("Script já aplicado, pulando: {caminho}", caminhoRelativo);
                continue;
            }

            string sqlContent = await File.ReadAllTextAsync(scriptPath, cancellationToken);
            var (reconhecido, jaExiste, descricao) = _databaseService.VerificarObjetoDdl(dbPath, sqlContent);
            if (reconhecido && jaExiste)
            {
                _logger.LogInformation("Script {caminho}: {descricao} já existe no banco -- registrando como aplicado sem executar.", caminhoRelativo, descricao);
                _databaseService.RegistrarScriptAplicado(dbPath, caminhoRelativo);
                continue;
            }

            _logger.LogInformation("Aplicando script: {caminho}", caminhoRelativo);
            try
            {
                await RunIsqlAsync(dbPath, scriptPath, cancellationToken);
            }
            catch (Exception ex)
            {
                string relatorio = MontarRelatorioErro(caminhoRelativo, posicao, scripts.Count, jaAplicadosAntes, reconhecido, descricao, ex);
                _logger.LogError("Script {caminho} falhou -- reportado à API, seguindo para o próximo. {relatorio}", caminhoRelativo, relatorio);
                // Sem versão/duração aqui: este log reporta a falha de UM script no meio do lote,
                // não a transição de versão completa -- essa (com sucesso ou erro) é reportada uma
                // vez só, no fim, por Worker.ProcessarAtualizacao.
                await _apiService.SendLog(cnpjCliente, sistema, "ERRO", relatorio);
                falhas++;
                continue;
            }

            _databaseService.RegistrarScriptAplicado(dbPath, caminhoRelativo);
        }

        if (falhas > 0)
            _logger.LogWarning("{falhas} script(s) falharam nesta rodada e foram pulados -- cada um já foi reportado à API individualmente.", falhas);

        return falhas;
    }

    private static string MontarRelatorioErro(string nomeArquivo, int posicao, int total, int jaAplicadosAntes, bool reconhecido, string descricaoDdl, Exception erroOriginal)
    {
        string verificacao = reconhecido
            ? $"verifiquei antes: {descricaoDdl} não existia no banco -- não é caso de 'já aplicado', é uma falha genuína ao tentar criar."
            : "não consegui identificar automaticamente o que esse script cria (não bate com os padrões CREATE TABLE/DOMAIN/GENERATOR/TRIGGER/INDEX nem ALTER TABLE ADD simples), então tentei executar direto.";

        return string.Join("\n",
            $"Falha ao aplicar script de atualização '{nomeArquivo}' ({posicao}/{total} do pacote; {jaAplicadosAntes} scripts já estavam aplicados antes deste lote).",
            $"Verificação prévia: {verificacao}",
            $"Erro retornado pelo isql: {erroOriginal.Message}");
    }

    private async Task RunIsqlAsync(string dbPath, string scriptPath, CancellationToken cancellationToken)
    {
        string connectionTarget = $"localhost/{_config.DbPort}:{dbPath}";

        // ISC_USER/ISC_PASSWORD via ambiente, não "-user"/"-password" na linha de comando --
        // mesmo motivo do gfix/gbak em Worker.cs (item 10 do RISCOS-CONHECIDOS.md): a linha de
        // comando de outro processo é visível localmente (Gerenciador de Tarefas, WMI), o
        // ambiente não. "-i" faz o isql tratar o script como entrada e sair sozinho ao final --
        // sem isso ele fica esperando comando interativo (igual acontecia com a tela do BScript).
        await _processService.RunProcessAsync(
            _config.IsqlPath,
            new[] { connectionTarget, "-i", scriptPath },
            ScriptTimeout,
            cancellationToken,
            new Dictionary<string, string> { ["ISC_USER"] = _config.DbUser, ["ISC_PASSWORD"] = _config.DbPassword });
    }
}
