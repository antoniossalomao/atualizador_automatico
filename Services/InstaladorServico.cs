using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace AtualizadorERP.Services;

/// <summary>
/// Registra/atualiza o próprio agente como serviço do Windows a partir da tela de configuração
/// (<see cref="AtualizadorERP.UI.SetupForm"/>) -- substitui os dois comandos manuais que o README
/// hoje pede pra rodar num PowerShell elevado depois de preencher o .ini na mão ("sc.exe create" +
/// "sc.exe start", ver README.md > "Instalando num cliente").
///
/// Sempre chama o "sc.exe" real via processo (mesmo padrão do <see cref="ProcessService"/> pros
/// binários do Firebird) em vez de um pacote NuGet tipo System.ServiceProcess.ServiceController:
/// é uma dependência a mais só pra "sc create"/"sc start"/"sc query", que o sc.exe já faz sozinho.
/// </summary>
public static class InstaladorServico
{
    public const string NomeServico = "AgenteAtualizadorERP";
    public const string NomeExibicao = "Agente Atualizador ERP";

    /// <summary>Argumento passado pro próprio .exe quando ele se relança elevado (ver
    /// <see cref="TentarRelancarElevado"/>) -- sinaliza pro Program.cs pular a tela e ir direto
    /// pra instalação, porque o atualizador.ini já foi salvo pelo processo NÃO elevado antes de
    /// pedir a elevação.</summary>
    public const string ArgumentoPosElevacao = "--instalar-servico";

    public static bool RodandoComoAdministrador()
    {
        using var identidade = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identidade).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool FoiChamadoPosElevacao(string[] args) =>
        args.Any(a => a.Equals(ArgumentoPosElevacao, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Relança o próprio executável pedindo elevação via UAC ("runas") com
    /// <see cref="ArgumentoPosElevacao"/>. Quem chamar deve encerrar o processo ATUAL logo depois
    /// (o processo elevado é totalmente independente, não um filho que devolve controle) -- o
    /// atualizador.ini precisa já estar salvo em disco ANTES desta chamada, porque é ele que o
    /// processo elevado vai ler.
    /// </summary>
    public static bool TentarRelancarElevado(out string? erro)
    {
        erro = null;
        string? caminhoExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(caminhoExe))
        {
            erro = "Não consegui determinar o caminho do próprio executável.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = caminhoExe,
                Arguments = ArgumentoPosElevacao,
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: clicou "Não" no prompt do UAC.
        {
            erro = "Elevação cancelada -- sem ela o serviço não pode ser instalado/atualizado. O atualizador.ini já foi salvo; pode tentar de novo, ou instalar manualmente (ver README).";
            return false;
        }
        catch (Win32Exception ex)
        {
            erro = $"Não consegui relançar elevado: {ex.Message}";
            return false;
        }
    }

    public readonly record struct ResultadoInstalacao(bool Sucesso, string Mensagem);

    /// <summary>
    /// Cria o serviço (primeira instalação) ou para+reinicia (já existia -- assim ele relê o
    /// atualizador.ini que acabou de ser salvo, já que <see cref="ConfiguracaoAgente"/> só lê o
    /// arquivo uma vez, na inicialização do processo). Assume que já está rodando elevado --
    /// checar isso é responsabilidade de quem chama (<see cref="RodandoComoAdministrador"/>).
    /// </summary>
    public static ResultadoInstalacao InstalarOuReiniciar(string caminhoExe)
    {
        var (codigoQuery, _, _) = ExecutarSc("query", NomeServico);
        bool servicoExiste = codigoQuery != 1060; // ERROR_SERVICE_DOES_NOT_EXIST

        if (!servicoExiste)
        {
            var (codigoCreate, _, erroCreate) = ExecutarSc(
                "create", NomeServico, "binPath=", caminhoExe, "start=", "auto", "DisplayName=", NomeExibicao);
            if (codigoCreate != 0)
                return new ResultadoInstalacao(false, $"Falha ao criar o serviço (sc create, código {codigoCreate}). {erroCreate}".Trim());
        }
        else
        {
            ExecutarSc("stop", NomeServico); // Falha aqui é esperada se já estava parado -- ignorada de propósito.
            AguardarParado(TimeSpan.FromSeconds(15));
        }

        var (codigoStart, _, erroStart) = ExecutarSc("start", NomeServico);
        // 1056 = ERROR_SERVICE_ALREADY_RUNNING -- corrida possível entre o "stop" acima (que pode
        // não ter concluído do ponto de vista do SCM) e este "start", não é falha de verdade.
        if (codigoStart != 0 && codigoStart != 1056)
            return new ResultadoInstalacao(false, $"Serviço {(servicoExiste ? "atualizado" : "criado")}, mas não consegui iniciar (sc start, código {codigoStart}). {erroStart}".Trim());

        return new ResultadoInstalacao(true, servicoExiste
            ? "Configuração salva e o serviço foi reiniciado com sucesso."
            : "Serviço instalado e iniciado com sucesso.");
    }

    private static void AguardarParado(TimeSpan tempoMaximo)
    {
        var cronometro = Stopwatch.StartNew();
        while (cronometro.Elapsed < tempoMaximo)
        {
            var (codigo, saida, _) = ExecutarSc("query", NomeServico);
            if (codigo == 1060 || saida.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(500);
        }
    }

    private static (int codigo, string saida, string erro) ExecutarSc(params string[] argumentos)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argumento in argumentos) info.ArgumentList.Add(argumento);

        using var processo = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar sc.exe.");
        string saida = processo.StandardOutput.ReadToEnd();
        string erro = processo.StandardError.ReadToEnd();
        processo.WaitForExit(30000);
        return (processo.ExitCode, saida, erro);
    }
}
