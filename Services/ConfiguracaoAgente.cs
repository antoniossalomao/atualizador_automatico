namespace AtualizadorERP.Services;

/// <summary>
/// Lê "atualizador.ini" ao lado do executável publicado. Substitui as variáveis de ambiente
/// usadas antes: setar variável de ambiente de um serviço Windows exige elevar e editar o
/// registro (HKLM\...\Services\{nome}\Environment), o que é inviável pra quem instala o agente
/// em dezenas de clientes em campo -- um .ini ao lado do .exe abre no Bloco de Notas.
///
/// Só o que realmente varia por cliente e não dá pra descobrir sozinho fica aqui (código do
/// cliente, token da API, sistema, credencial do Firebird). Caminhos de banco/backup/trabalho têm
/// default relativo à própria pasta do agente -- convenção real: o agente mora dentro da pasta do cliente (ex.:
/// Bredas\Atualizador\), com JUNIOR.fdb/BEXE.fdb no mesmo nível de onde o BEXE_FDB resolver (por
/// padrão, um nível acima da pasta do agente). Cada caminho também aceita override explícito no
/// próprio .ini, pra clientes cuja estrutura fugir do padrão.
/// </summary>
public class ConfiguracaoAgente
{
    public string CodigoCliente { get; }
    public string Sistema { get; }
    public string ApiUrl { get; }
    public string ApiToken { get; }
    public string DbUser { get; }
    public string DbPassword { get; }
    public string DbPort { get; }
    public string JuniorFdbPath { get; }
    public string BexeFdbPath { get; }
    public string GfixPath { get; }
    public string GbakPath { get; }
    public string IsqlPath { get; }

    /// <summary>Pasta de trabalho descartável (downloads/extração/backup em andamento) -- fica
    /// dentro da própria pasta do agente, subpasta "_trabalho". É apagada e recriada a cada Fase 1
    /// nova; só o conteúdo de "pacotes\" dentro dela é o que a Fase 4 varre atrás de *.exe.</summary>
    public string PastaTrabalho { get; }

    /// <summary>Pasta de backups PERSISTENTE (nunca é varrida/apagada por limpeza automática) --
    /// fora de PastaTrabalho de propósito, pra não ser confundida com lixo descartável. Guarda os
    /// últimos <see cref="BackupsParaManter"/> ciclos bem-sucedidos (pré + pós cada um).</summary>
    public string PastaBackups { get; }

    public int BackupsParaManter { get; }

    private const string NomeArquivoPadrao = "atualizador.ini";

    public ConfiguracaoAgente() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, NomeArquivoPadrao))
    {
    }

    internal ConfiguracaoAgente(string caminhoIni)
    {
        if (!File.Exists(caminhoIni))
            throw new InvalidOperationException(
                $"Arquivo de configuração não encontrado: {caminhoIni} -- crie um '{NomeArquivoPadrao}' ao lado do " +
                "executável (comece copiando atualizador.ini.example) com pelo menos CODIGO_CLIENTE, SISTEMA, " +
                "API_TOKEN e DB_PASSWORD preenchidos. Ver README.md.");

        var valores = LerIni(caminhoIni);
        string pastaAgente = Path.GetDirectoryName(Path.GetFullPath(caminhoIni))!;

        CodigoCliente = Obrigatorio(valores, "CODIGO_CLIENTE", caminhoIni);
        Sistema = Obrigatorio(valores, "SISTEMA", caminhoIni);
        ApiToken = Obrigatorio(valores, "API_TOKEN", caminhoIni);
        DbPassword = Obrigatorio(valores, "DB_PASSWORD", caminhoIni);

        ApiUrl = ComDefault(valores, "API_URL", "http://localhost:3000/api");
        DbUser = ComDefault(valores, "DB_USER", "SYSDBA");
        DbPort = ComDefault(valores, "DB_PORT", "3050");
        GfixPath = ComDefault(valores, "GFIX_PATH", @"C:\Program Files (x86)\Firebird\Firebird_2_5\bin\gfix.exe");
        GbakPath = ComDefault(valores, "GBAK_PATH", @"C:\Program Files (x86)\Firebird\Firebird_2_5\bin\gbak.exe");
        IsqlPath = ComDefault(valores, "ISQL_PATH", @"C:\Program Files (x86)\Firebird\Firebird_2_5\bin\isql.exe");

        JuniorFdbPath = CaminhoComDefault(valores, "JUNIOR_FDB", pastaAgente, "..", "JUNIOR.FDB");
        BexeFdbPath = CaminhoComDefault(valores, "BEXE_FDB", pastaAgente, "..", "BEXE.FDB");
        PastaTrabalho = CaminhoComDefault(valores, "PASTA_TRABALHO", pastaAgente, "_trabalho");
        PastaBackups = CaminhoComDefault(valores, "PASTA_BACKUPS", pastaAgente, "Backups");

        BackupsParaManter = int.TryParse(valores.GetValueOrDefault("BACKUPS_PARA_MANTER"), out var n) && n > 0 ? n : 10;
    }

    // Construtor usado só pelos testes de integração, que não têm um atualizador.ini real em
    // disco e precisam de um JUNIOR/BEXE/pasta de trabalho próprios por teste (bancos Firebird
    // descartáveis, um por teste) -- ver AtualizadorERP.Tests/TestAmbiente.cs.
    internal ConfiguracaoAgente(
        string codigoCliente, string sistema, string apiUrl, string apiToken,
        string dbUser, string dbPassword, string dbPort,
        string juniorFdbPath, string bexeFdbPath,
        string gfixPath, string gbakPath, string isqlPath,
        string pastaTrabalho, string pastaBackups, int backupsParaManter)
    {
        CodigoCliente = codigoCliente;
        Sistema = sistema;
        ApiUrl = apiUrl;
        ApiToken = apiToken;
        DbUser = dbUser;
        DbPassword = dbPassword;
        DbPort = dbPort;
        JuniorFdbPath = juniorFdbPath;
        BexeFdbPath = bexeFdbPath;
        GfixPath = gfixPath;
        GbakPath = gbakPath;
        IsqlPath = isqlPath;
        PastaTrabalho = pastaTrabalho;
        PastaBackups = pastaBackups;
        BackupsParaManter = backupsParaManter;
    }

    private static string Obrigatorio(Dictionary<string, string> valores, string chave, string caminhoIni)
    {
        if (!valores.TryGetValue(chave, out var valor) || string.IsNullOrWhiteSpace(valor))
            throw new InvalidOperationException($"Defina {chave} em {caminhoIni} antes de iniciar o agente.");
        return valor;
    }

    private static string ComDefault(Dictionary<string, string> valores, string chave, string padrao)
        => valores.TryGetValue(chave, out var valor) && !string.IsNullOrWhiteSpace(valor) ? valor : padrao;

    private static string CaminhoComDefault(Dictionary<string, string> valores, string chave, string pastaAgente, params string[] relativoPadrao)
    {
        if (valores.TryGetValue(chave, out var valor) && !string.IsNullOrWhiteSpace(valor))
            return Path.IsPathRooted(valor) ? valor : Path.GetFullPath(Path.Combine(pastaAgente, valor));
        return Path.GetFullPath(Path.Combine(new[] { pastaAgente }.Concat(relativoPadrao).ToArray()));
    }

    // Parser mínimo de propósito: "CHAVE=valor" por linha, comentários com ";" ou "#", seções
    // "[Nome]" ignoradas (só organizam visualmente o arquivo, sem efeito no parsing -- não há
    // necessidade real de escopo por seção pra uma dúzia de chaves flat).
    private static Dictionary<string, string> LerIni(string caminho)
    {
        var valores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var linhaCrua in File.ReadAllLines(caminho))
        {
            string linha = linhaCrua.Trim();
            if (linha.Length == 0 || linha.StartsWith(';') || linha.StartsWith('#') || linha.StartsWith('['))
                continue;
            int separador = linha.IndexOf('=');
            if (separador <= 0) continue;
            string chave = linha[..separador].Trim();
            string valor = linha[(separador + 1)..].Trim();
            valores[chave] = valor;
        }
        return valores;
    }
}
