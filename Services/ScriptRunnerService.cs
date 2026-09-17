using System.Text.RegularExpressions;

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
    /// Aplica, em ordem alfabética de nome de arquivo, todo ".sql" "raiz" encontrado dentro de
    /// <paramref name="pacotesPath"/> que ainda não esteja em SYS_ATUALIZACAO/SCRIPTS. "Raiz" aqui
    /// NÃO é <c>SearchOption.TopDirectoryOnly</c> em cima de <paramref name="pacotesPath"/> --
    /// conferido contra o pacote real do B_Vendas (via "7za l"): o pacote nunca solta .sql direto
    /// nele, sempre embrulha tudo numa pasta por sistema (ex.: "Scripts-BVendas\", ao lado de
    /// "Dlls-BVendas\", espelhando a própria estrutura da pasta do cliente). Uma primeira versão
    /// desta correção usava TopDirectoryOnly ali e não encontrava NENHUM script -- pior que o bug
    /// original. <see cref="EhScriptRaiz"/> aceita .sql solto direto em
    /// <paramref name="pacotesPath"/> OU um nível abaixo (dentro de uma pasta como
    /// "Scripts-BVendas\"), mas não dois níveis (ex.: "Scripts-BVendas\scripts2012\") -- essas
    /// subpastas mais fundas são o ARQUIVO histórico do BScript.exe (script já rodado há anos pela
    /// tela, mantido só de referência), não pendência nova. Rodando por engano os de lá, um cujo
    /// objeto não bate com os padrões reconhecidos por <see cref="DatabaseService.VerificarObjetoDdl"/>
    /// falha o isql à toa e polui o relatório com dezenas de "erro" que nunca foram pendência de
    /// verdade (visto num pacote real: 25 scripts de "scripts2012"/"scripts2015" reportados como
    /// falha, todos já aplicados manualmente décadas atrás). Essas subpastas continuam indo para a
    /// pasta do cliente normalmente -- ver Worker.CopiarParaPastaCliente, que copia tudo
    /// recursivamente -- só não são candidatas a EXECUÇÃO aqui.
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
    public async Task<int> RunPendingScriptsAsync(string dbPath, string pacotesPath, string codigoCliente, string sistema, CancellationToken cancellationToken = default)
    {
        var scripts = Directory.GetFiles(pacotesPath, "*.sql", SearchOption.AllDirectories)
            .Where(caminho => EhScriptRaiz(pacotesPath, caminho))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (scripts.Count == 0)
        {
            _logger.LogInformation("Nenhum script .sql \"raiz\" no pacote desta versão (subpastas mais fundas, se houver, não contam -- são arquivo histórico).");
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
            // Só nome do arquivo, não caminho relativo: com a busca restrita à raiz (ver acima),
            // todo script já está direto em pacotesPath -- não existe mais subpasta para
            // desambiguar, e nomeArquivo já bate igual com o que o BScript.exe grava manualmente
            // em SCRIPTS (que só conhece o nome, nunca uma subpasta).
            string nomeArquivo = Path.GetFileName(scriptPath);
            if (jaAplicados.Contains(nomeArquivo))
            {
                _logger.LogInformation("Script já aplicado, pulando: {nome}", nomeArquivo);
                continue;
            }

            // Lista deliberada em ConfiguracaoAgente.ScriptsIgnorados -- pra scripts legados
            // conhecidos como quebrados de origem (achado inspecionando os 1027 scripts reais do
            // B_Vendas: um "Altera_Procedure_X.sql" que era só o corpo solto, sem o cabeçalho
            // "ALTER PROCEDURE ... AS"), onde nenhuma correção automática resolve porque o
            // próprio arquivo está incompleto. Nunca roda, nunca reporta erro, nunca registra em
            // SCRIPTS -- fica pendente pra sempre até alguém corrigir o arquivo de origem e tirar
            // da lista.
            if (_config.ScriptsIgnorados.Contains(nomeArquivo, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Script ignorado por configuração (SCRIPTS_IGNORADOS), pulando sem reportar: {nome}", nomeArquivo);
                continue;
            }

            string sqlContent = await File.ReadAllTextAsync(scriptPath, cancellationToken);
            var (reconhecido, jaExiste, descricao) = _databaseService.VerificarObjetoDdl(dbPath, sqlContent);
            if (reconhecido && jaExiste)
            {
                _logger.LogInformation("Script {nome}: {descricao} já existe no banco -- registrando como aplicado sem executar.", nomeArquivo, descricao);
                _databaseService.RegistrarScriptAplicado(dbPath, nomeArquivo);
                continue;
            }

            _logger.LogInformation("Aplicando script: {nome}", nomeArquivo);
            try
            {
                await PrepararScriptParaIsqlAsync(scriptPath, sqlContent, cancellationToken);
                await RunIsqlAsync(dbPath, scriptPath, cancellationToken);
            }
            catch (Exception ex) when (ObjetoJaExisteNoErro(ex))
            {
                // Cobre o mesmo cenário do pré-check em VerificarObjetoDdl (objeto criado décadas
                // atrás, nunca registrado em SCRIPTS), mas para tipos de DDL que o pré-check não
                // reconhece (CREATE EXCEPTION, CREATE PROCEDURE etc.) -- ali "reconhecido" já vem
                // false, então o isql roda de verdade e é ELE quem revela que o objeto já existe.
                // Não é falha genuína: sincroniza o controle com a realidade do banco, sem poluir
                // o relatório da API com um erro que nunca foi pendência de verdade.
                _logger.LogInformation("Script {nome}: objeto já existe no banco (isql: {mensagem}) -- registrando como aplicado sem reportar à API.", nomeArquivo, ex.Message);
                _databaseService.RegistrarScriptAplicado(dbPath, nomeArquivo);
                continue;
            }
            catch (Exception ex)
            {
                string relatorio = MontarRelatorioErro(nomeArquivo, posicao, scripts.Count, jaAplicadosAntes, reconhecido, descricao, ex);
                _logger.LogError("Script {nome} falhou -- reportado à API, seguindo para o próximo. {relatorio}", nomeArquivo, relatorio);
                // Sem versão/duração aqui: este log reporta a falha de UM script no meio do lote,
                // não a transição de versão completa -- essa (com sucesso ou erro) é reportada uma
                // vez só, no fim, por Worker.ProcessarAtualizacao.
                await _apiService.SendLog(codigoCliente, sistema, "ERRO", relatorio, fase: "scripts");
                falhas++;
                continue;
            }

            _databaseService.RegistrarScriptAplicado(dbPath, nomeArquivo);
        }

        if (falhas > 0)
            _logger.LogWarning("{falhas} script(s) falharam nesta rodada e foram pulados -- cada um já foi reportado à API individualmente.", falhas);

        return falhas;
    }

    /// <summary>
    /// True se <paramref name="caminhoScript"/> está solto direto em <paramref name="pacotesPath"/>
    /// ou um nível abaixo (ex.: "Scripts-BVendas\X.sql") -- false se estiver dois níveis ou mais
    /// (ex.: "Scripts-BVendas\scripts2012\X.sql", arquivo histórico, ver comentário de
    /// <see cref="RunPendingScriptsAsync"/>).
    /// </summary>
    private static bool EhScriptRaiz(string pacotesPath, string caminhoScript)
    {
        string relativo = Path.GetRelativePath(pacotesPath, caminhoScript);
        int separadores = relativo.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
        return separadores <= 1;
    }

    // CREATE/ALTER/RECREATE TRIGGER|PROCEDURE e EXECUTE BLOCK têm corpo BEGIN...END com ";"
    // internos (um por linha do corpo) -- sem o próprio script definir outro terminador (SET
    // TERM), o isql usa ";" como terminador padrão e quebra o comando no primeiro ";" de DENTRO
    // do corpo, gerando uma cascata de erros de sintaxe (-104/-206) em vez de UM erro só.
    // BScript.exe/IBExpert não têm esse problema (mandam o texto inteiro pra API do Firebird como
    // um comando só, sem parser de terminador). Confirmado contra os 1027 scripts reais do
    // B_Vendas: todo CREATE/ALTER TRIGGER/PROCEDURE sem "SET TERM" próprio falhava assim -- 45
    // deles só nesse único pacote. Só envolve quando o script ainda NÃO define seu próprio
    // terminador (alguns mais novos já trazem "SET TERM" -- envolver de novo aninharia o comando
    // e quebraria esses).
    // "CREATE\s+(?:OR\s+ALTER\s+)?" antes de "ALTER" sozinho: sem o ramo composto, "CREATE OR
    // ALTER TRIGGER X" casava a partir do "ALTER" (a busca por posição mais à esquerda encontra
    // "CREATE " mas falha ali porque depois vem "OR", não "TRIGGER"/"PROCEDURE", e só then acha
    // "ALTER TRIGGER" mais adiante) -- cortando o comando ao meio ("CREATE OR " de um lado,
    // "ALTER TRIGGER..." do outro, sintaxe inválida). Com o ramo composto, a busca já casa a
    // partir do "CREATE", que é a posição correta.
    private static readonly Regex PadraoPrecisaSetTerm = new(
        @"\bCREATE\s+(?:OR\s+ALTER\s+)?(?:TRIGGER|PROCEDURE)\b|\bALTER\s+(?:TRIGGER|PROCEDURE)\b|\bRECREATE\s+(?:TRIGGER|PROCEDURE)\b|\bEXECUTE\s+BLOCK\b",
        RegexOptions.IgnoreCase);

    // BScript.exe e o IBExpert também executam o SQL sem exigir ";" final -- confirmado contra
    // Firebird real: o MESMO "ALTER TABLE ... ADD ... DEFAULT 'False'" sem ";" falha no isql (sem
    // erro de sintaxe -- chega no fim do arquivo com o comando ainda "aberto" e devolve
    // "unexpected end of command"), mas com o ";" roda limpo e cria a coluna. Sobrescreve o
    // arquivo dentro da pasta de trabalho (não o pacote original baixado) antes do isql ler --
    // seja envolvendo com SET TERM (scripts de trigger/procedure) ou só garantindo o terminador
    // final (os demais).
    private static async Task PrepararScriptParaIsqlAsync(string scriptPath, string sqlContent, CancellationToken cancellationToken)
    {
        string aparado = sqlContent.TrimEnd();
        if (aparado.Length == 0) return;

        bool jaTemSetTerm = sqlContent.Contains("SET TERM", StringComparison.OrdinalIgnoreCase);
        var match = jaTemSetTerm ? null : PadraoPrecisaSetTerm.Match(aparado);

        if (match is { Success: true })
        {
            // Só envolve A PARTIR do comando com corpo -- tudo ANTES dele fica intacto, sob o
            // terminador ";" padrão. Achado num script real: "DROP TRIGGER X;\nSET SQL DIALECT
            // 3;\nSET NAMES ISO8859_1;\n\nCREATE OR ALTER TRIGGER X ... AS BEGIN...END" -- uma
            // primeira versão desta correção envolvia o ARQUIVO INTEIRO, engolindo o ";" desses
            // comandos anteriores (viravam texto dentro do "^") e quebrando o parser logo no
            // "SET SQL DIALECT" (erro "Token unknown ... SET"). Só o comando com corpo (a partir
            // daqui) precisa do terminador alternativo.
            string antes = aparado[..match.Index];
            string comando = aparado[match.Index..];
            if (comando.EndsWith(';')) comando = comando[..^1];
            await File.WriteAllTextAsync(scriptPath, $"{antes}SET TERM ^ ;\n{comando}^\nSET TERM ; ^\n", cancellationToken);
        }
        else if (!jaTemSetTerm && !aparado.EndsWith(';'))
        {
            // A quebra de linha antes do ";" evita que ele seja engolido por um "--comentário"
            // sem quebra de linha no fim do arquivo.
            await File.WriteAllTextAsync(scriptPath, aparado + "\n;\n", cancellationToken);
        }
    }

    // Firebird responde em inglês independente do locale da instância, mas nem todo tipo de
    // objeto duplicado usa a mensagem amigável -- confirmado contra o Firebird 2.5 real desta
    // máquina: CREATE TABLE/VIEW/PROCEDURE duplicado dá "-Table X already exists", mas CREATE
    // GENERATOR/EXCEPTION duplicado cai direto no erro de baixo nível da violação do índice único
    // do catálogo de sistema ("unsuccessful metadata update" + "attempt to store duplicate value
    // ... in unique index"), sem a palavra "already exists" em lugar nenhum.
    private static bool ObjetoJaExisteNoErro(Exception ex)
    {
        string mensagem = ex.Message;
        if (mensagem.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return true;

        return mensagem.Contains("unsuccessful metadata update", StringComparison.OrdinalIgnoreCase)
            && mensagem.Contains("attempt to store duplicate value", StringComparison.OrdinalIgnoreCase);
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
        //
        // "-ch ISO8859_1": mesmo charset que DatabaseService.GetConnectionString já usa (ver
        // comentário lá) -- sem ele, o isql conecta com o charset padrão da instalação, que não
        // bate com os scripts legados que trazem seu próprio "SET NAMES ISO8859_1" (convenção
        // "_ANSI" nos nomes de arquivo, achada nos scripts reais do B_Vendas). Confirmado contra
        // Firebird real: o MESMO script falha com "Malformed string" sem "-ch" e roda limpo com.
        await _processService.RunProcessAsync(
            _config.IsqlPath,
            new[] { connectionTarget, "-i", scriptPath, "-ch", "ISO8859_1" },
            ScriptTimeout,
            cancellationToken,
            new Dictionary<string, string> { ["ISC_USER"] = _config.DbUser, ["ISC_PASSWORD"] = _config.DbPassword });
    }
}
