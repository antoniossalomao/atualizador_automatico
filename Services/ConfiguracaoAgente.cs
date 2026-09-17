namespace AtualizadorERP.Services;

/// <summary>Um sistema que este agente pode cuidar, e o nome do executável que identifica se o
/// cliente atual realmente tem esse sistema instalado (ex.: Nome="B_NFe", NomeExeEsperado=
/// "B_NFE.exe" -- o nome do sistema no painel e o nome do arquivo real não batem sempre, então
/// não dá pra inferir um a partir do outro).</summary>
public sealed record SistemaConfigurado(string Nome, string NomeExeEsperado);

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

    /// <summary>Todos os sistemas que ESTA INSTALAÇÃO DO AGENTE conhece (não necessariamente os
    /// que este cliente tem) -- uma instância só, não uma por sistema: na prática, um cliente tem
    /// um JUNIOR.fdb/BEXE.fdb só servindo vários produtos (confirmado abrindo um BEXE.fdb real com
    /// três produtos na mesma tabela EXECUTAVEIS), então instâncias separadas por sistema
    /// brigariam pela mesma linha de SYS_ATUALIZACAO e pelos mesmos gfix/gbak no mesmo banco.
    ///
    /// O Worker filtra essa lista a cada ciclo, checando qual <see cref="SistemaConfigurado.NomeExeEsperado"/>
    /// realmente existe na pasta do cliente -- só baixa/aplica atualização dos sistemas que esse
    /// cliente específico tem instalado. Sem esse filtro, publicar uma versão nova de QUALQUER
    /// sistema faria TODO cliente (mesmo os que nunca tiveram aquele sistema) tentar baixá-la.</summary>
    public IReadOnlyList<SistemaConfigurado> Sistemas { get; }

    /// <summary>Quais sistemas de <see cref="Sistemas"/> têm permissão de rodar script contra o
    /// JUNIOR.fdb (Fase 2/3 completa: espera AUTORIZADO, gfix -shut, backup, ScriptRunnerService).
    /// Todo o resto de Sistemas é tratado como "só troca de executável": nunca passa pelo
    /// ScriptRunnerService, mesmo que o pacote baixado contenha .sql -- confirmado que alguns
    /// pacotes (ex.: BImportaXML) trazem .sql junto por herança de empacotamento, mas rodá-los
    /// quebra o banco. Não dá pra inferir "precisa de script" pelo conteúdo do pacote; só uma lista
    /// explícita e deliberada é segura aqui.</summary>
    public IReadOnlyList<string> SistemasComScript { get; }

    /// <summary>Nomes de arquivo (ex.: "20241009Altera_Procedure_Inventario_NFCe.sql") que o
    /// ScriptRunnerService nunca tenta rodar, mesmo que apareçam pendentes num pacote -- pra
    /// scripts legados conhecidos como quebrados de origem (ex.: um corpo de procedure salvo sem
    /// o cabeçalho "ALTER PROCEDURE", achado inspecionando os scripts reais do B_Vendas), onde
    /// nenhuma correção automática resolve porque o próprio arquivo está incompleto. Comparado só
    /// pelo nome do arquivo (não pelo conteúdo) -- é a mesma chave que SCRIPTS já usa.</summary>
    public IReadOnlyList<string> ScriptsIgnorados { get; }

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
                "executável (comece copiando atualizador.ini.example) com pelo menos CODIGO_CLIENTE, SISTEMAS, " +
                "API_TOKEN e DB_PASSWORD preenchidos. Ver README.md.");

        var valores = LerIni(caminhoIni);
        string pastaAgente = Path.GetDirectoryName(Path.GetFullPath(caminhoIni))!;

        CodigoCliente = Obrigatorio(valores, "CODIGO_CLIENTE", caminhoIni);
        Sistemas = ListaDeSistemas(valores, "SISTEMAS", caminhoIni);
        // Opcional, default vazio: um cliente que só distribui .exe (nenhum sistema com script)
        // não precisa preencher isso.
        SistemasComScript = ListaOpcional(valores, "SISTEMAS_COM_SCRIPT");
        // Opcional, default vazio -- a maioria dos clientes não tem nenhum script legado quebrado
        // pra ignorar.
        ScriptsIgnorados = ListaOpcional(valores, "SCRIPTS_IGNORADOS");
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
        string codigoCliente, IReadOnlyList<SistemaConfigurado> sistemas, IReadOnlyList<string> sistemasComScript, string apiUrl, string apiToken,
        string dbUser, string dbPassword, string dbPort,
        string juniorFdbPath, string bexeFdbPath,
        string gfixPath, string gbakPath, string isqlPath,
        string pastaTrabalho, string pastaBackups, int backupsParaManter,
        IReadOnlyList<string>? scriptsIgnorados = null)
    {
        CodigoCliente = codigoCliente;
        Sistemas = sistemas;
        SistemasComScript = sistemasComScript;
        ScriptsIgnorados = scriptsIgnorados ?? Array.Empty<string>();
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

    // "B_Vendas:B_Vendas.exe,B_NFe:B_NFE.exe" -> [(B_Vendas, B_Vendas.exe), (B_NFe, B_NFE.exe)] --
    // vírgula separa sistemas, dois-pontos separa nome do sistema do nome do exe esperado. Par
    // completo (não só o nome do sistema) porque o nome cadastrado no painel e o nome do arquivo
    // real quase nunca batem (ex.: sistema "B_Importa", arquivo "BImportaXML.exe").
    private static IReadOnlyList<SistemaConfigurado> ListaDeSistemas(Dictionary<string, string> valores, string chave, string caminhoIni)
    {
        string bruto = Obrigatorio(valores, chave, caminhoIni);
        var itens = bruto.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (itens.Length == 0)
            throw new InvalidOperationException($"Defina {chave} em {caminhoIni} antes de iniciar o agente.");

        var resultado = new List<SistemaConfigurado>();
        foreach (var item in itens)
        {
            int separador = item.IndexOf(':');
            if (separador <= 0 || separador == item.Length - 1)
                throw new InvalidOperationException(
                    $"Entrada inválida em {chave} ({caminhoIni}): '{item}' -- esperado 'NomeDoSistema:NomeDoExecutavel.exe' " +
                    "(ex.: 'B_Vendas:B_Vendas.exe'). Ver atualizador.ini.example.");
            resultado.Add(new SistemaConfigurado(item[..separador].Trim(), item[(separador + 1)..].Trim()));
        }
        return resultado;
    }

    private static IReadOnlyList<string> ListaOpcional(Dictionary<string, string> valores, string chave)
    {
        if (!valores.TryGetValue(chave, out var bruto) || string.IsNullOrWhiteSpace(bruto))
            return Array.Empty<string>();
        return bruto.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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
