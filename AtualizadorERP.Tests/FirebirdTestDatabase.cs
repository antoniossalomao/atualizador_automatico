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
        Porta = "3050";
        _connectionString = $"User={Usuario};Password={Senha};Database={caminhoArquivo};DataSource=localhost;Port={Porta};Dialect=3;Charset=ISO8859_1;Pooling=false;";
    }

    /// <summary>Schema mínimo assumido de um JUNIOR.fdb -- ver RISCOS-CONHECIDOS.md ("Schema do
    /// JUNIOR.fdb -- parcialmente confirmado"): SYS_ATUALIZACAO não existe no schema real (o
    /// próprio agente cria via DatabaseService.GarantirTabelaSysAtualizacao -- ver
    /// CriarJuniorSemSysAtualizacao para testar esse caminho), SCRIPTS foi confirmada.</summary>
    public static FirebirdTestDatabase CriarJunior(string status = "AUTORIZADO", string versaoAtual = "1.0.0", string versaoNova = "1.0.0")
    {
        var db = CriarJuniorSemSysAtualizacao();
        db.ExecutarNaoConsulta(@"
            CREATE TABLE SYS_ATUALIZACAO (
                ID INTEGER NOT NULL PRIMARY KEY,
                STATUS VARCHAR(20),
                VERSAO_NOVA VARCHAR(50),
                VERSAO_ATUAL VARCHAR(50),
                MENSAGEM_LOG VARCHAR(500)
            )");
        db.ExecutarNaoConsulta(
            "INSERT INTO SYS_ATUALIZACAO (ID, STATUS, VERSAO_NOVA, VERSAO_ATUAL) VALUES (1, @status, @versaoNova, @versaoAtual)",
            ("@status", status), ("@versaoNova", versaoNova), ("@versaoAtual", versaoAtual));
        return db;
    }

    /// <summary>Reproduz um JUNIOR.fdb real antes da primeira execução do agente: SCRIPTS existe
    /// (o BScript.exe já a mantinha), SYS_ATUALIZACAO ainda não -- é o cenário que
    /// DatabaseService.GarantirTabelaSysAtualizacao precisa cobrir.</summary>
    public static FirebirdTestDatabase CriarJuniorSemSysAtualizacao()
    {
        var db = Criar();
        db.ExecutarNaoConsulta("CREATE GENERATOR SEQUENCIA_SCRIPTS");
        db.ExecutarNaoConsulta(@"
            CREATE TABLE SCRIPTS (
                ID INTEGER NOT NULL PRIMARY KEY,
                NOME_ARQUIVO VARCHAR(255),
                TIPO_EXECUCAO VARCHAR(10),
                DATA_EXECUCAO TIMESTAMP
            )");
        return db;
    }

    /// <summary>Schema do EXECUTAVEIS confirmado campo a campo contra um BEXE.fdb real correto
    /// ("BEXE_certo.FDB", 03/09/2026): NOMEARQUIVO guarda caminho completo (por isso 300, não 100,
    /// caracteres), VERSAO guarda a versão embutida no executável (até 60 caracteres reais),
    /// DATA_ATUALIZACAO é DATE (sem hora), e VERSAOATUALIZADA é um flag texto ("True"/"False") --
    /// por isso só cabem 5 caracteres reais (RDB$FIELD_LENGTH bate em 20 bytes UTF8,
    /// RDB$CHARACTER_LENGTH em 5), não uma data de versão como se assumia antes (item 14 do
    /// RISCOS-CONHECIDOS.md).</summary>
    public static FirebirdTestDatabase CriarBexe()
    {
        var db = Criar();
        db.ExecutarNaoConsulta(@"
            CREATE TABLE EXECUTAVEIS (
                NOMEPRODUTO VARCHAR(50),
                NOMEARQUIVO VARCHAR(300) NOT NULL PRIMARY KEY,
                EXECUTAVEL BLOB,
                HASHEXE VARCHAR(64),
                VERSAO VARCHAR(60),
                VERSAOATUALIZADA VARCHAR(5) CHARACTER SET UTF8,
                DATA_ATUALIZACAO DATE
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
