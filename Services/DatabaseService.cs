using System.Security.Cryptography;
using FirebirdSql.Data.FirebirdClient;

namespace AtualizadorERP.Services;

public class DatabaseService
{
    private string GetConnectionString(string dbPath)
    {
        string user = Environment.GetEnvironmentVariable("ATUALIZADOR_DB_USER") ?? "SYSDBA";
        string password = Environment.GetEnvironmentVariable("ATUALIZADOR_DB_PASSWORD") ?? "";
        // Porta configurável: um cliente real inspecionado usa 3051, não o 3050 padrão --
        // deixar isso fixo faria o agente nunca conseguir conectar nesse tipo de ambiente.
        string port = Environment.GetEnvironmentVariable("ATUALIZADOR_DB_PORT") ?? "3050";
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Defina ATUALIZADOR_DB_PASSWORD antes de acessar o Firebird.");
        return $"User={user};Password={password};Database={dbPath};DataSource=localhost;Port={port};Dialect=3;Charset=WIN1252;";
    }

    public string GetStatusAtualizacao(string dbPath)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand("SELECT STATUS FROM SYS_ATUALIZACAO WHERE ID = 1", conn);
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "CONCLUIDO";
    }

    public string GetVersaoAtual(string dbPath)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand("SELECT VERSAO_NOVA FROM SYS_ATUALIZACAO WHERE ID = 1", conn);
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "0.0.0";
    }

    /// <summary>
    /// "mensagem" grava em MENSAGEM_LOG (coluna que existia na tabela mas nunca era escrita).
    /// Sem isso, se o servidor do cliente perder acesso à internet no momento de uma falha, não
    /// sobra nenhum rastro local do que aconteceu -- o único log ficava só na API central.
    /// </summary>
    public void SetStatusAtualizacao(string dbPath, string status, string? versaoNova, string? mensagem = null)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();

        var sets = new List<string> { "STATUS = @status" };
        if (versaoNova != null) sets.Add("VERSAO_NOVA = @versao");
        if (mensagem != null) sets.Add("MENSAGEM_LOG = @mensagem");
        string sql = $"UPDATE SYS_ATUALIZACAO SET {string.Join(", ", sets)} WHERE ID = 1";

        using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.AddWithValue("@status", status);
        if (versaoNova != null) cmd.Parameters.AddWithValue("@versao", versaoNova);
        if (mensagem != null) cmd.Parameters.AddWithValue("@mensagem", mensagem.Length > 500 ? mensagem[..500] : mensagem);

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Schema confirmado abrindo um BEXE.FDB real de produção (engenharia reversa dos metadados
    /// do Firebird): a tabela é EXECUTAVEIS, com os campos NOMEPRODUTO, NOMEARQUIVO, VERSAO,
    /// VERSAOATUALIZADA, EXECUTAVEL (BLOB) e HASHEXE -- não "VERSOES_EXE"/"NOME_EXE"/
    /// "ARQUIVO_BLOB" como este código assumia antes. NOMEPRODUTO usa o nome do arquivo sem
    /// extensão como valor padrão; ajuste aqui se a convenção real do cliente for outra. VERSAO
    /// e VERSAOATUALIZADA recebem a mesma versão nova porque este agente não tem como saber,
    /// por executável individual, qual era a versão anterior de cada um.
    ///
    /// Todos os arquivos entram numa única transação: ou o lote inteiro é confirmado, ou nenhum
    /// -- evita terminais lendo uma mistura de binários antigos e novos se um arquivo no meio
    /// do lote falhar.
    /// </summary>
    public void InjetarNovosBinarios(string bexeDbPath, string tempPath, string versaoNova)
    {
        var arquivosExe = Directory.GetFiles(tempPath, "*.exe", SearchOption.AllDirectories);

        using var conn = new FbConnection(GetConnectionString(bexeDbPath));
        conn.Open();
        using var transaction = conn.BeginTransaction();
        try
        {
            foreach (var arquivo in arquivosExe)
            {
                string nomeArquivo = Path.GetFileName(arquivo);
                string nomeProduto = Path.GetFileNameWithoutExtension(arquivo);
                byte[] fileBytes = File.ReadAllBytes(arquivo);
                string hash = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

                using var cmdUpdate = new FbCommand(
                    "UPDATE EXECUTAVEIS SET EXECUTAVEL = @exe, HASHEXE = @hash, VERSAO = @versao, VERSAOATUALIZADA = @versaoAtualizada, DATA_ATUALIZACAO = @data WHERE NOMEARQUIVO = @nome",
                    conn, transaction);
                cmdUpdate.Parameters.Add("@exe", FbDbType.Binary).Value = fileBytes;
                cmdUpdate.Parameters.AddWithValue("@hash", hash);
                cmdUpdate.Parameters.AddWithValue("@versao", versaoNova);
                cmdUpdate.Parameters.AddWithValue("@versaoAtualizada", versaoNova);
                cmdUpdate.Parameters.AddWithValue("@data", DateTime.Now);
                cmdUpdate.Parameters.AddWithValue("@nome", nomeArquivo);
                int affected = cmdUpdate.ExecuteNonQuery();

                if (affected == 0)
                {
                    using var cmdInsert = new FbCommand(
                        "INSERT INTO EXECUTAVEIS (NOMEPRODUTO, NOMEARQUIVO, EXECUTAVEL, HASHEXE, VERSAO, VERSAOATUALIZADA, DATA_ATUALIZACAO) VALUES (@produto, @nome, @exe, @hash, @versao, @versaoAtualizada, @data)",
                        conn, transaction);
                    cmdInsert.Parameters.AddWithValue("@produto", nomeProduto);
                    cmdInsert.Parameters.AddWithValue("@nome", nomeArquivo);
                    cmdInsert.Parameters.Add("@exe", FbDbType.Binary).Value = fileBytes;
                    cmdInsert.Parameters.AddWithValue("@hash", hash);
                    cmdInsert.Parameters.AddWithValue("@versao", versaoNova);
                    cmdInsert.Parameters.AddWithValue("@versaoAtualizada", versaoNova);
                    cmdInsert.Parameters.AddWithValue("@data", DateTime.Now);
                    cmdInsert.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}
