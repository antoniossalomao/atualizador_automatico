using FirebirdSql.Data.FirebirdClient;

namespace AtualizadorERP.Tests;

/// <summary>
/// Banco Firebird descartável, criado do zero a cada teste contra o Firebird real instalado
/// nesta máquina (não uma cópia de produção -- nada de dado de cliente aqui, só o schema mínimo
/// que o código assume). Cada teste recebe um arquivo próprio em pasta temporária, apagado no
/// Dispose -- não entra no repositório (.gitignore já bloqueia *.fdb).
/// </summary>
public sealed class FirebirdTestDatabase : IDisposable
{
    public const string Usuario = "SYSDBA";
    public const string Senha = "masterkey";

    public string CaminhoArquivo { get; }
    public string Porta { get; }

    private readonly string _connectionString;

    private FirebirdTestDatabase(string caminhoArquivo)
    {
        CaminhoArquivo = caminhoArquivo;
        Porta = Environment.GetEnvironmentVariable("ATUALIZADOR_DB_PORT") ?? "3050";
        _connectionString = $"User={Usuario};Password={Senha};Database={caminhoArquivo};DataSource=localhost;Port={Porta};Dialect=3;Charset=ISO8859_1;Pooling=false;";
    }

    /// <summary>Schema mínimo assumido de um JUNIOR.fdb -- ver RISCOS-CONHECIDOS.md ("Schema do
    /// JUNIOR.fdb -- parcialmente confirmado"): SYS_ATUALIZACAO não foi confirmada contra um
    /// banco real, SCRIPTS foi.</summary>
    public static FirebirdTestDatabase CriarJunior(string status = "AUTORIZADO", string versaoAtual = "1.0.0", string versaoNova = "1.0.0")
    {
        var db = Criar();
        db.ExecutarNaoConsulta(@"
            CREATE TABLE SYS_ATUALIZACAO (
                ID INTEGER NOT NULL PRIMARY KEY,
                STATUS VARCHAR(20),
                VERSAO_NOVA VARCHAR(20),
                VERSAO_ATUAL VARCHAR(20),
                MENSAGEM_LOG VARCHAR(500)
            )");
        db.ExecutarNaoConsulta("CREATE GENERATOR SEQUENCIA_SCRIPTS");
        db.ExecutarNaoConsulta(@"
            CREATE TABLE SCRIPTS (
                ID INTEGER NOT NULL PRIMARY KEY,
                NOME_ARQUIVO VARCHAR(255),
                TIPO_EXECUCAO VARCHAR(10),
                DATA_EXECUCAO TIMESTAMP
            )");
        db.ExecutarNaoConsulta(
            "INSERT INTO SYS_ATUALIZACAO (ID, STATUS, VERSAO_NOVA, VERSAO_ATUAL) VALUES (1, @status, @versaoNova, @versaoAtual)",
            ("@status", status), ("@versaoNova", versaoNova), ("@versaoAtual", versaoAtual));
        return db;
    }

    /// <summary>Schema do EXECUTAVEIS confirmado por engenharia reversa de um BEXE.fdb real (ver
    /// item 14 do RISCOS-CONHECIDOS.md): VERSAOATUALIZADA é VARCHAR(20) declarado, mas em UTF8 só
    /// cabem 5 caracteres reais -- reproduzido aqui com o mesmo charset, não um VARCHAR comum.</summary>
    public static FirebirdTestDatabase CriarBexe()
    {
        var db = Criar();
        db.ExecutarNaoConsulta(@"
            CREATE TABLE EXECUTAVEIS (
                NOMEPRODUTO VARCHAR(50),
                NOMEARQUIVO VARCHAR(100) NOT NULL PRIMARY KEY,
                EXECUTAVEL BLOB,
                HASHEXE VARCHAR(64),
                VERSAO VARCHAR(20),
                VERSAOATUALIZADA VARCHAR(20) CHARACTER SET UTF8,
                DATA_ATUALIZACAO TIMESTAMP
            )");
        return db;
    }

    private static FirebirdTestDatabase Criar()
    {
        string caminho = Path.Combine(Path.GetTempPath(), $"atualizador_teste_{Guid.NewGuid():N}.fdb");
        var db = new FirebirdTestDatabase(caminho);
        FbConnection.CreateDatabase(db._connectionString);
        return db;
    }

    public void ExecutarNaoConsulta(string sql, params (string Nome, object Valor)[] parametros)
    {
        using var conn = new FbConnection(_connectionString);
        conn.Open();
        using var cmd = new FbCommand(sql, conn);
        foreach (var (nome, valor) in parametros) cmd.Parameters.AddWithValue(nome, valor);
        cmd.ExecuteNonQuery();
    }

    public object? ExecutarEscalar(string sql)
    {
        using var conn = new FbConnection(_connectionString);
        conn.Open();
        using var cmd = new FbCommand(sql, conn);
        return cmd.ExecuteScalar();
    }

    public int ContarLinhas(string sql)
    {
        using var conn = new FbConnection(_connectionString);
        conn.Open();
        using var cmd = new FbCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        int total = 0;
        while (reader.Read()) total++;
        return total;
    }

    public void Dispose()
    {
        try
        {
            FbConnection.ClearPool(new FbConnection(_connectionString));
            if (File.Exists(CaminhoArquivo)) File.Delete(CaminhoArquivo);
        }
        catch
        {
            // Melhor esforço: é um arquivo de teste descartável numa pasta temporária.
        }
    }
}
