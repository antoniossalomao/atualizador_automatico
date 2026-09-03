using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;

namespace AtualizadorERP.Services;

public class DatabaseService
{
    private readonly ConfiguracaoAgente _config;

    public DatabaseService(ConfiguracaoAgente config)
    {
        _config = config;
    }

    private string GetConnectionString(string dbPath)
    {
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
        return $"User={_config.DbUser};Password={_config.DbPassword};Database={dbPath};DataSource=localhost;Port={_config.DbPort};Dialect=3;Charset=ISO8859_1;Pooling=false;";
    }

    /// <summary>
    /// Cria a tabela SYS_ATUALIZACAO em JUNIOR.fdb se ela ainda não existir. Confirmado contra uma
    /// cópia real de produção (366 tabelas) que o schema não a tem -- ver RISCOS-CONHECIDOS.md,
    /// "Schema do JUNIOR.fdb". Em vez de depender de um DBA rodar essa DDL manualmente em cada
    /// cliente antes da primeira instalação do agente, o próprio Worker garante o schema no
    /// arranque: idempotente (checa RDB$RELATIONS antes de criar), então rodar de novo num cliente
    /// que já tem a tabela é um no-op. A linha ID=1 inicial nasce em CONCLUIDO -- mesmo estado que
    /// GetStatusAtualizacao já assume como padrão quando a tabela nem existia.
    /// </summary>
    public void GarantirTabelaSysAtualizacao(string dbPath)
    {
        if (ExisteRegistroSistema(dbPath, "RDB$RELATIONS", "RDB$RELATION_NAME", "SYS_ATUALIZACAO")) return;

        using var conn = new FbConnection(GetConnectionString(dbPath));
        conn.Open();

        // VERSAO_NOVA/VERSAO_ATUAL em VARCHAR(50): essa tabela não existe na base real (é este
        // projeto que a cria, ver RISCOS-CONHECIDOS.md), então não há schema legado a respeitar
        // aqui -- só uma margem confortável para o formato de versão do painel (ex.
        // "2026.08.27"), sem risco de truncar no futuro.
        using (var cmdCreate = new FbCommand(@"
            CREATE TABLE SYS_ATUALIZACAO (
                ID INTEGER NOT NULL PRIMARY KEY,
                STATUS VARCHAR(20),
                VERSAO_NOVA VARCHAR(50),
                VERSAO_ATUAL VARCHAR(50),
                MENSAGEM_LOG VARCHAR(500)
            )", conn))
        {
            cmdCreate.ExecuteNonQuery();
        }

        using var cmdInsert = new FbCommand(
            "INSERT INTO SYS_ATUALIZACAO (ID, STATUS, VERSAO_NOVA, VERSAO_ATUAL) VALUES (1, 'CONCLUIDO', '0.0.0', '0.0.0')",
            conn);
        cmdInsert.ExecuteNonQuery();
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
    /// Schema confirmado campo a campo contra um BEXE.fdb real de produção correto
    /// ("BEXE_certo.FDB", 03/09/2026) -- a tabela é EXECUTAVEIS, com NOMEPRODUTO, NOMEARQUIVO,
    /// VERSAO, VERSAOATUALIZADA, EXECUTAVEL (BLOB) e HASHEXE. Quatro campos gravavam errado numa
    /// versão anterior deste método (comparado direto contra o arquivo real):
    ///
    ///   - NOMEARQUIVO: caminho COMPLETO no disco do cliente (ex. "D:\Bredas\B_Vendas.exe"), não
    ///     só o nome solto. Usa a pasta onde o próprio BEXE.fdb está (não a do agente) como
    ///     referência -- é lá que o executável correspondente também mora, na convenção real.
    ///   - HASHEXE: SHA-1 maiúsculo (40 caracteres hex), não SHA-256.
    ///   - VERSAO: a versão EMBUTIDA no próprio executável (FileVersion do Delphi, ex.
    ///     "26.9.1.8"), não a versão do pacote da API. Confirmado comparando o FileVersion real
    ///     do B_Vendas.exe baixado contra a linha correspondente do BEXE_certo.FDB.
    ///   - VERSAOATUALIZADA: um FLAG booleano em texto ("True"), não um número de versão -- por
    ///     isso a coluna real só cabe 5 caracteres ("False" tem 5), o que já tinha aparecido no
    ///     item 14 do RISCOS-CONHECIDOS.md sem essa explicação na época.
    ///
    /// Todos os arquivos entram numa única transação: ou o lote inteiro é confirmado, ou nenhum
    /// -- evita terminais lendo uma mistura de binários antigos e novos se um arquivo no meio
    /// do lote falhar.
    /// </summary>
    public void InjetarNovosBinarios(string bexeDbPath, string tempPath, string versaoNova)
    {
        // TopDirectoryOnly, não AllDirectories: testado com um pacote real (B_Vendas 2026.09.01)
        // que trazia openssl.exe dentro de Dlls-BVendas\ -- uma dependência de terceiros usada
        // pelo próprio ERP para HTTPS, não um produto a distribuir. Uma varredura recursiva
        // injetava os dois no BEXE.fdb como se fossem duas "versões novas" iguais, e um terminal
        // baixaria o openssl.exe achando que era uma atualização do ERP. A convenção real do
        // pacote é: o(s) executável(is) do produto ficam soltos na raiz, dependências/DLLs/scripts
        // ficam em subpastas.
        var arquivosExe = Directory.GetFiles(tempPath, "*.exe", SearchOption.TopDirectoryOnly);

        // Sem isso, um pacote cuja extração não produziu nenhum executável (7z corrompido,
        // pacote vazio, extensão inesperada) commitava uma transação vazia aqui e o Worker
        // seguia pra CONCLUIDO como se tivesse atualizado alguma coisa -- sem erro, sem log, sem
        // nenhum sinal de que os terminais não vão receber nada de novo.
        if (arquivosExe.Length == 0)
            throw new InvalidOperationException($"Pacote da versão {versaoNova} não continha nenhum executável (*.exe) para distribuir em {tempPath} -- abortando em vez de marcar como concluído sem ter atualizado nada.");

        string pastaCliente = Path.GetDirectoryName(Path.GetFullPath(bexeDbPath))
            ?? throw new InvalidOperationException($"Não consegui determinar a pasta do cliente a partir de {bexeDbPath}.");

        using var conn = new FbConnection(GetConnectionString(bexeDbPath));
        conn.Open();
        using var transaction = conn.BeginTransaction();
        try
        {
            foreach (var arquivo in arquivosExe)
            {
                string nomeArquivoCompleto = Path.Combine(pastaCliente, Path.GetFileName(arquivo));
                string nomeProduto = Path.GetFileNameWithoutExtension(arquivo);
                byte[] fileBytes = File.ReadAllBytes(arquivo);
                string hash = Convert.ToHexString(SHA1.HashData(fileBytes));
                // Cai pra versaoNova só se o próprio arquivo não tiver versão embutida -- não
                // deveria acontecer com um binário real do Delphi, mas evita gravar VERSAO vazio.
                string versaoArquivo = FileVersionInfo.GetVersionInfo(arquivo).FileVersion ?? versaoNova;

                using var cmdUpdate = new FbCommand(
                    "UPDATE EXECUTAVEIS SET EXECUTAVEL = @exe, HASHEXE = @hash, VERSAO = @versao, VERSAOATUALIZADA = 'True', DATA_ATUALIZACAO = @data WHERE NOMEARQUIVO = @nome",
                    conn, transaction);
                cmdUpdate.Parameters.Add("@exe", FbDbType.Binary).Value = fileBytes;
                cmdUpdate.Parameters.AddWithValue("@hash", hash);
                cmdUpdate.Parameters.AddWithValue("@versao", versaoArquivo);
                cmdUpdate.Parameters.AddWithValue("@data", DateTime.Now);
                cmdUpdate.Parameters.AddWithValue("@nome", nomeArquivoCompleto);
                int affected = cmdUpdate.ExecuteNonQuery();

                if (affected == 0)
                {
                    using var cmdInsert = new FbCommand(
                        "INSERT INTO EXECUTAVEIS (NOMEPRODUTO, NOMEARQUIVO, EXECUTAVEL, HASHEXE, VERSAO, VERSAOATUALIZADA, DATA_ATUALIZACAO) VALUES (@produto, @nome, @exe, @hash, @versao, 'True', @data)",
                        conn, transaction);
                    cmdInsert.Parameters.AddWithValue("@produto", nomeProduto);
                    cmdInsert.Parameters.AddWithValue("@nome", nomeArquivoCompleto);
                    cmdInsert.Parameters.Add("@exe", FbDbType.Binary).Value = fileBytes;
                    cmdInsert.Parameters.AddWithValue("@hash", hash);
                    cmdInsert.Parameters.AddWithValue("@versao", versaoArquivo);
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
