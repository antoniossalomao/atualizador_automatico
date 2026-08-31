using AtualizadorERP.Services;
using Xunit;

namespace AtualizadorERP.Tests;

/// <summary>
/// Testes contra Firebird real (não mockado) -- é a única forma de confirmar SQL, tipos de
/// coluna e limites de charset de verdade, como os achados dos itens 1 e 14 do
/// RISCOS-CONHECIDOS.md, que nenhuma leitura de código pegaria.
/// </summary>
public class DatabaseServiceTests
{
    private readonly DatabaseService _databaseService = new();

    public DatabaseServiceTests() => TestAmbiente.Garantir();

    [Fact]
    public void GetStatusAtualizacao_le_o_status_gravado()
    {
        using var junior = FirebirdTestDatabase.CriarJunior(status: "PENDENTE");
        Assert.Equal("PENDENTE", _databaseService.GetStatusAtualizacao(junior.CaminhoArquivo));
    }

    [Fact]
    public void SetStatusAtualizacao_atualiza_status_versao_e_mensagem()
    {
        using var junior = FirebirdTestDatabase.CriarJunior();
        _databaseService.SetStatusAtualizacao(junior.CaminhoArquivo, "ERRO", "2.0.0", "mensagem de teste");

        Assert.Equal("ERRO", _databaseService.GetStatusAtualizacao(junior.CaminhoArquivo));
        Assert.Equal("2.0.0", _databaseService.GetVersaoAtual(junior.CaminhoArquivo));
        Assert.Equal("mensagem de teste", junior.ExecutarEscalar("SELECT MENSAGEM_LOG FROM SYS_ATUALIZACAO WHERE ID = 1"));
    }

    [Fact]
    public void SetStatusAtualizacao_trunca_mensagem_acima_de_500_caracteres()
    {
        using var junior = FirebirdTestDatabase.CriarJunior();
        string mensagemGigante = new string('x', 800);

        _databaseService.SetStatusAtualizacao(junior.CaminhoArquivo, "ERRO", null, mensagemGigante);

        var gravado = (string)junior.ExecutarEscalar("SELECT MENSAGEM_LOG FROM SYS_ATUALIZACAO WHERE ID = 1")!;
        Assert.Equal(500, gravado.TrimEnd().Length);
    }

    [Fact]
    public void ConfirmarVersaoAtual_so_avanca_apos_chamada_explicita()
    {
        using var junior = FirebirdTestDatabase.CriarJunior(versaoAtual: "1.0.0", versaoNova: "1.0.0");
        _databaseService.SetStatusAtualizacao(junior.CaminhoArquivo, "PROCESSANDO", "9.9.9");

        // Antes de confirmar: VERSAO_ATUAL não mudou, mesmo com VERSAO_NOVA já apontando pra
        // frente -- é exatamente essa separação que substitui o antigo versao_anterior.txt
        // (item 6 do RISCOS-CONHECIDOS.md).
        Assert.Equal("1.0.0", _databaseService.GetVersaoConfirmada(junior.CaminhoArquivo));

        _databaseService.ConfirmarVersaoAtual(junior.CaminhoArquivo);
        Assert.Equal("9.9.9", _databaseService.GetVersaoConfirmada(junior.CaminhoArquivo));
    }

    [Fact]
    public void GetScriptsAplicados_reflete_RegistrarScriptAplicado()
    {
        using var junior = FirebirdTestDatabase.CriarJunior();
        Assert.Empty(_databaseService.GetScriptsAplicados(junior.CaminhoArquivo));

        _databaseService.RegistrarScriptAplicado(junior.CaminhoArquivo, @"scripts2012\Cria_tabela_x.sql");

        var aplicados = _databaseService.GetScriptsAplicados(junior.CaminhoArquivo);
        Assert.Contains(@"scripts2012\Cria_tabela_x.sql", aplicados);
    }

    [Fact]
    public void VerificarObjetoDdl_reconhece_create_table_e_detecta_se_ja_existe()
    {
        using var junior = FirebirdTestDatabase.CriarJunior();

        var antes = _databaseService.VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE FOO (ID INTEGER)");
        Assert.True(antes.Reconhecido);
        Assert.False(antes.JaExiste);

        junior.ExecutarNaoConsulta("CREATE TABLE FOO (ID INTEGER)");

        var depois = _databaseService.VerificarObjetoDdl(junior.CaminhoArquivo, "CREATE TABLE FOO (ID INTEGER)");
        Assert.True(depois.Reconhecido);
        Assert.True(depois.JaExiste);
    }

    [Fact]
    public void VerificarObjetoDdl_reconhece_alter_table_add_coluna()
    {
        using var junior = FirebirdTestDatabase.CriarJunior();

        var antes = _databaseService.VerificarObjetoDdl(junior.CaminhoArquivo, "ALTER TABLE SCRIPTS ADD NOVA_COLUNA INTEGER");
        Assert.True(antes.Reconhecido);
        Assert.False(antes.JaExiste);

        junior.ExecutarNaoConsulta("ALTER TABLE SCRIPTS ADD NOVA_COLUNA INTEGER");

        var depois = _databaseService.VerificarObjetoDdl(junior.CaminhoArquivo, "ALTER TABLE SCRIPTS ADD NOVA_COLUNA INTEGER");
        Assert.True(depois.Reconhecido);
        Assert.True(depois.JaExiste);
    }

    [Fact]
    public void VerificarObjetoDdl_nao_reconhece_ddl_fora_dos_padroes_cobertos()
    {
        using var junior = FirebirdTestDatabase.CriarJunior();
        var resultado = _databaseService.VerificarObjetoDdl(junior.CaminhoArquivo, "DROP TABLE FOO");
        Assert.False(resultado.Reconhecido);
    }

    [Fact]
    public void InjetarNovosBinarios_lanca_se_pacote_nao_tem_nenhum_exe()
    {
        using var bexe = FirebirdTestDatabase.CriarBexe();
        string pastaVazia = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                _databaseService.InjetarNovosBinarios(bexe.CaminhoArquivo, pastaVazia, "9.9.9"));
            Assert.Contains("nenhum executável", ex.Message);
        }
        finally
        {
            Directory.Delete(pastaVazia, true);
        }
    }

    [Fact]
    public void InjetarNovosBinarios_lanca_se_versao_estoura_o_limite_real_de_5_caracteres()
    {
        // Reproduz o achado do item 14: VERSAOATUALIZADA é VARCHAR(20) só no nome -- em UTF8 só
        // cabem 5 caracteres de verdade. O formato do painel ("2026.08.27", 10 chars) nunca coube.
        using var bexe = FirebirdTestDatabase.CriarBexe();
        string pasta = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(pasta, "produto.exe"), new byte[] { 1, 2, 3 });

            var ex = Assert.Throws<InvalidOperationException>(() =>
                _databaseService.InjetarNovosBinarios(bexe.CaminhoArquivo, pasta, "2026.08.27"));
            Assert.Contains("VERSAOATUALIZADA", ex.Message);
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }

    [Fact]
    public void InjetarNovosBinarios_insere_na_primeira_vez_e_atualiza_na_segunda()
    {
        using var bexe = FirebirdTestDatabase.CriarBexe();
        string pasta = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string caminhoExe = Path.Combine(pasta, "produto.exe");
            File.WriteAllBytes(caminhoExe, new byte[] { 1, 2, 3, 4 });

            _databaseService.InjetarNovosBinarios(bexe.CaminhoArquivo, pasta, "9.9.9");
            Assert.Equal(1, bexe.ContarLinhas("SELECT 1 FROM EXECUTAVEIS WHERE NOMEARQUIVO = 'produto.exe'"));
            string hash1 = (string)bexe.ExecutarEscalar("SELECT HASHEXE FROM EXECUTAVEIS WHERE NOMEARQUIVO = 'produto.exe'")!;

            File.WriteAllBytes(caminhoExe, new byte[] { 9, 9, 9, 9, 9 });
            _databaseService.InjetarNovosBinarios(bexe.CaminhoArquivo, pasta, "9.9.9");

            // Continua uma linha só (UPDATE, não INSERT duplicado) e o hash mudou junto com o
            // conteúdo do arquivo.
            Assert.Equal(1, bexe.ContarLinhas("SELECT 1 FROM EXECUTAVEIS WHERE NOMEARQUIVO = 'produto.exe'"));
            string hash2 = (string)bexe.ExecutarEscalar("SELECT HASHEXE FROM EXECUTAVEIS WHERE NOMEARQUIVO = 'produto.exe'")!;
            Assert.NotEqual(hash1, hash2);
        }
        finally
        {
            Directory.Delete(pasta, true);
        }
    }
}
