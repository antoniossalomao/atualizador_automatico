using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
        // ISO8859_1, não WIN1252: testado contra uma cópia real de JUNIOR.fdb, o
        // FirebirdSql.Data.FirebirdClient 10.0.0 rejeita "WIN1252" (e qualquer variação de
        // grafia) com ArgumentException "Invalid character set specified" -- o agente nunca
        // conseguia abrir conexão nenhuma, em nenhum ambiente. ISO8859_1 cobre os mesmos
        // acentos do português (á é í ó ú ã õ ç ê); só diverge do WIN1252 numa faixa de
        // caracteres especiais (aspas curvas, €, ™) que não aparecem em nome de cliente/produto.
        // Pooling=false: testado e reproduzido -- uma conexão aberta e fechada ANTES de um
        // "gfix -shut" fica em cache no pool do driver; a PRÓXIMA chamada com essa mesma
        // connection string tenta reaproveitar essa conexão (agora inválida) em vez de abrir uma
        // nova, e falha com "database ... shutdown" mesmo com o banco em modo "multi" (que
        // deveria permitir conexão SYSDBA nova). Cada método aqui já abre e fecha sua própria
        // conexão por chamada -- pooling nunca trouxe benefício de performance neste serviço,
        // só esse risco durante a janela crítica da Fase 3.
        return $"User={user};Password={password};Database={dbPath};DataSource=localhost;Port={port};Dialect=3;Charset=ISO8859_1;Pooling=false;";
    }

    public string GetStatusAtualizacao(string dbPath)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand("SELECT STATUS FROM SYS_ATUALIZACAO WHERE ID = 1", conn);
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "CONCLUIDO";
    }

    /// <summary>
    /// VERSAO_NOVA: a versão que está (ou acabou de ficar) sendo perseguida por esta tentativa --
    /// alvo em PENDENTE/PROCESSANDO, e o valor que InjetarNovosBinarios usa pra rotular os
    /// binários recém-aplicados. Não é "a versão que o cliente está rodando agora" -- pra isso,
    /// ver GetVersaoConfirmada.
    /// </summary>
    public string GetVersaoAtual(string dbPath)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand("SELECT VERSAO_NOVA FROM SYS_ATUALIZACAO WHERE ID = 1", conn);
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "0.0.0";
    }

    /// <summary>
    /// VERSAO_ATUAL: a última versão CONFIRMADA (Fase 3 concluída com sucesso de verdade -- ver
    /// ConfirmarVersaoAtual). É o que o Worker manda pra API no polling, e nunca muda durante uma
    /// tentativa em andamento ou que falhou -- diferente de VERSAO_NOVA, que já vira o alvo assim
    /// que a Fase 1 encontra uma atualização. Substitui o antigo versao_anterior.txt: como
    /// VERSAO_ATUAL só avança em caso de sucesso, uma falha não precisa "reverter" nada, porque
    /// ela nunca chegou a mudar. Coluna nova, adicionada em cima do schema já assumido (não
    /// confirmado) de SYS_ATUALIZACAO -- ver RISCOS-CONHECIDOS.md.
    /// </summary>
    public string GetVersaoConfirmada(string dbPath)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand("SELECT VERSAO_ATUAL FROM SYS_ATUALIZACAO WHERE ID = 1", conn);
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "0.0.0";
    }

    public void ConfirmarVersaoAtual(string dbPath)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand("UPDATE SYS_ATUALIZACAO SET VERSAO_ATUAL = VERSAO_NOVA WHERE ID = 1", conn);
        cmd.ExecuteNonQuery();
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
    /// Schema confirmado abrindo um JUNIOR.fdb real de produção: a tabela é SCRIPTS, com os
    /// campos ID, NOME_ARQUIVO, TIPO_EXECUCAO e DATA_EXECUCAO -- é o próprio controle que o
    /// BScript.exe usa pra saber "quais scripts já rodei nesse banco". Reaproveitá-la em vez de
    /// criar uma tabela própria mantém compatível o histórico que o BScript já gravou manualmente
    /// em cada cliente.
    /// </summary>
    public HashSet<string> GetScriptsAplicados(string dbPath)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand("SELECT NOME_ARQUIVO FROM SCRIPTS", conn);
        using var reader = cmd.ExecuteReader();
        var aplicados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) aplicados.Add(reader.GetString(0).Trim());
        return aplicados;
    }

    // Cobre os padrões observados nos ~2300 scripts reais inspecionados (Cria_tabela, Cria_campo,
    // Cria_generator, Cria_domain, Cria_Trigger, Cria_index). Scripts com múltiplos comandos ou
    // um DDL fora dessa lista (DROP, CREATE VIEW, CREATE PROCEDURE com corpo complexo etc.) caem
    // no "não reconhecido" de propósito -- é mais seguro tentar executar e reportar um erro real
    // do que arriscar um falso positivo de "já existe" que faz o agente pular uma mudança
    // pendente de verdade.
    private static readonly (Regex Padrao, string Tipo)[] PadroesDdlReconhecidos =
    {
        (new Regex(@"CREATE\s+TABLE\s+""?(\w+)""?", RegexOptions.IgnoreCase), "TABELA"),
        (new Regex(@"CREATE\s+DOMAIN\s+""?(\w+)""?", RegexOptions.IgnoreCase), "DOMAIN"),
        (new Regex(@"CREATE\s+(?:GENERATOR|SEQUENCE)\s+""?(\w+)""?", RegexOptions.IgnoreCase), "GENERATOR"),
        (new Regex(@"CREATE\s+TRIGGER\s+""?(\w+)""?", RegexOptions.IgnoreCase), "TRIGGER"),
        (new Regex(@"CREATE\s+(?:UNIQUE\s+)?(?:ASC(?:ENDING)?\s+|DESC(?:ENDING)?\s+)?INDEX\s+""?(\w+)""?", RegexOptions.IgnoreCase), "INDICE"),
    };

    private static readonly Regex PadraoAlterAddColuna = new(@"ALTER\s+TABLE\s+""?(\w+)""?\s+ADD\s+""?(\w+)""?\s", RegexOptions.IgnoreCase);

    /// <summary>
    /// Olha o texto do script (não o nome do arquivo) e tenta descobrir, via consulta às tabelas
    /// de sistema do Firebird, se o objeto que ele cria/altera já existe -- o mesmo "sinal" que um
    /// humano usa pra saber, na tela do BScript.exe, que um script antigo já foi aplicado antes de
    /// existir controle na tabela SCRIPTS (confirmado contra um banco real: domain MEMOTEXTO já
    /// existia e o script de 2005 que o criava nunca tinha sido registrado).
    /// </summary>
    public (bool Reconhecido, bool JaExiste, string Descricao) VerificarObjetoDdl(string dbPath, string sqlContent)
    {
        foreach (var (padrao, tipo) in PadroesDdlReconhecidos)
        {
            var match = padrao.Match(sqlContent);
            if (!match.Success) continue;

            string nome = match.Groups[1].Value.ToUpperInvariant();
            bool existe = tipo switch
            {
                "TABELA" => ExisteRegistroSistema(dbPath, "RDB$RELATIONS", "RDB$RELATION_NAME", nome),
                "DOMAIN" => ExisteRegistroSistema(dbPath, "RDB$FIELDS", "RDB$FIELD_NAME", nome),
                "GENERATOR" => ExisteRegistroSistema(dbPath, "RDB$GENERATORS", "RDB$GENERATOR_NAME", nome),
                "TRIGGER" => ExisteRegistroSistema(dbPath, "RDB$TRIGGERS", "RDB$TRIGGER_NAME", nome),
                "INDICE" => ExisteRegistroSistema(dbPath, "RDB$INDICES", "RDB$INDEX_NAME", nome),
                _ => false,
            };
            return (true, existe, $"{tipo} '{nome}'");
        }

        var alterMatch = PadraoAlterAddColuna.Match(sqlContent);
        if (alterMatch.Success)
        {
            string tabela = alterMatch.Groups[1].Value.ToUpperInvariant();
            string coluna = alterMatch.Groups[2].Value.ToUpperInvariant();
            bool existe = ExisteColuna(dbPath, tabela, coluna);
            return (true, existe, $"coluna '{coluna}' na tabela '{tabela}'");
        }

        return (false, false, "tipo de script não reconhecido automaticamente (não é CREATE TABLE/DOMAIN/GENERATOR/TRIGGER/INDEX nem ALTER TABLE ADD simples)");
    }

    private bool ExisteRegistroSistema(string dbPath, string tabelaSistema, string colunaNome, string valor)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand($"SELECT 1 FROM {tabelaSistema} WHERE {colunaNome} = @valor", conn);
        cmd.Parameters.AddWithValue("@valor", valor);
        return cmd.ExecuteScalar() != null;
    }

    private bool ExisteColuna(string dbPath, string tabela, string coluna)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand("SELECT 1 FROM RDB$RELATION_FIELDS WHERE RDB$RELATION_NAME = @tabela AND RDB$FIELD_NAME = @coluna", conn);
        cmd.Parameters.AddWithValue("@tabela", tabela);
        cmd.Parameters.AddWithValue("@coluna", coluna);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// GEN_ID(SEQUENCIA_SCRIPTS, 1): o gerador que a própria tabela SCRIPTS já usa para o ID --
    /// confirmado no mesmo banco real. TIPO_EXECUCAO grava "automatica" (valor que já existia no
    /// histórico, ao lado de "manual"), então dá pra distinguir depois o que o agente aplicou
    /// sozinho do que uma pessoa aplicou pela tela do BScript.
    /// </summary>
    public void RegistrarScriptAplicado(string dbPath, string nomeArquivo)
    {
        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();
        using var cmd = new FbCommand(
            "INSERT INTO SCRIPTS (ID, NOME_ARQUIVO, TIPO_EXECUCAO, DATA_EXECUCAO) VALUES (GEN_ID(SEQUENCIA_SCRIPTS, 1), @nome, 'automatica', CURRENT_TIMESTAMP)",
            conn);
        cmd.Parameters.AddWithValue("@nome", nomeArquivo);
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

        // Sem isso, um pacote cuja extração não produziu nenhum executável (7z corrompido,
        // pacote vazio, extensão inesperada) commitava uma transação vazia aqui e o Worker
        // seguia pra CONCLUIDO como se tivesse atualizado alguma coisa -- sem erro, sem log, sem
        // nenhum sinal de que os terminais não vão receber nada de novo.
        if (arquivosExe.Length == 0)
            throw new InvalidOperationException($"Pacote da versão {versaoNova} não continha nenhum executável (*.exe) para distribuir em {tempPath} -- abortando em vez de marcar como concluído sem ter atualizado nada.");

        // VERSAOATUALIZADA é declarada VARCHAR(20), mas em UTF8 -- RDB$CHARACTER_LENGTH real é só
        // 5 (confirmado contra um BEXE.fdb real). "2026.08.27" (o formato de versão que o painel
        // web usa) nunca coube: o INSERT/UPDATE estourava "string right truncation", um erro
        // Firebird genérico que não deixa claro qual coluna nem por quê. Checar aqui antes convert
        // isso num erro específico e acionável em vez de deixar o SQL falhar de forma opaca.
        const int MaxCaracteresVersaoAtualizada = 5;
        if (versaoNova.Length > MaxCaracteresVersaoAtualizada)
            throw new InvalidOperationException(
                $"Versão '{versaoNova}' tem {versaoNova.Length} caracteres, mas EXECUTAVEIS.VERSAOATUALIZADA só cabe {MaxCaracteresVersaoAtualizada} (schema real do BEXE.fdb, coluna UTF8 apesar do nome sugerir 20) -- " +
                "não dá pra truncar sem risco de duas versões diferentes virarem o mesmo valor pros terminais. Precisa de uma convenção de versão mais curta, ou o schema do BEXE.fdb desse cliente precisa mudar.");

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
