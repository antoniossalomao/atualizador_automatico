# Como mexer neste projeto

Guia para quem vai alterar o agente. O [README](README.md) explica **o que** o
agente faz e como instalá-lo; este arquivo explica **como trabalhar** no código.

> **Antes de qualquer coisa, leia [RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md).**
> Não é formalidade: este programa roda sem supervisão no servidor do cliente e
> tem poder de parar o banco de dados da empresa dele. Uma alteração inocente no
> lugar errado deixa um cliente sem trabalhar.

## Preparar a máquina

- **.NET SDK 8.0 ou mais novo** (o projeto tem como alvo `net8.0`).
- **Firebird 2.5** instalado, com `gfix`, `gbak` e `isql` disponíveis — os
  testes rodam contra Firebird **de verdade**, não contra mock.

```bash
dotnet build AtualizadorERP.sln     # compila agente + testes
dotnet test  AtualizadorERP.sln     # 39 testes
```

O build precisa terminar com **0 warnings** — e o CI usa `-warnaserror`, então
warning lá é build quebrado. Warning que ninguém vê é dívida que ninguém paga.

### Analisadores

O nível padrão do .NET 8 já passa limpo. Além dele, o `.editorconfig` promove a
erro **três regras escolhidas uma a uma**, por pegarem defeito de verdade neste
contexto:

| Regra | Por que ela importa aqui |
|---|---|
| `CA2016` | `CancellationToken` não propagado. Num serviço do Windows é a diferença entre parar quando mandam parar e o gerenciador de serviços matar o processo achando que travou. Já pegou a conferência de hash de pacote. |
| `CA5350`/`CA5351` | Algoritmo criptográfico fraco. Há **um** uso legítimo de SHA1 (o campo `HASHEXE`, exigido pelo formato do ERP legado), suprimido no próprio ponto com a justificativa escrita. Qualquer uso novo tem que doer. |
| `CA1869` | `JsonSerializerOptions` recriado a cada chamada joga fora o cache de metadados de tipo que o .NET guarda dentro da instância. |

Subir o `AnalysisMode` inteiro foi considerado e descartado: `Minimum` traz 14
avisos e `Recommended` traz 114, quase todos irrelevantes aqui — `ConfigureAwait`
num worker que não tem contexto de sincronização, `LoggerMessage` por
microperformance. **Um portão com 114 avisos não é lido por ninguém**, e portão
que ninguém lê não é portão.

Ao suprimir uma regra, suprima **no ponto exato** (`#pragma warning disable`) e
escreva o porquê ali. Suprimir no `.editorconfig`, para o projeto todo, apaga a
informação de que aquele caso era especial.

## Rodar os testes

```bash
dotnet test AtualizadorERP.sln
```

Os testes criam bancos Firebird descartáveis do zero — nunca cópias de
produção. O ciclo completo de ponta a ponta (Fase 3 → Fase 4) está automatizado
em `WorkerIntegrationTests`, que chama `Worker.ProcessarAtualizacao` diretamente
(daí os métodos `internal` e o `InternalsVisibleTo` em `AssemblyInfo.cs`).

**A suíte tem uma instabilidade conhecida sob carga.** Rodando os 39 testes
seguidos, um teste do `WorkerIntegrationTests` falha de vez em quando —
observado em `Falha_na_fase_4_reverte_o_banco_via_backup_e_nao_promove_versao`.
O mesmo teste passa isolado, e a execução seguinte da suíte completa passa
inteira.

Medido em set/2026: **2 falhas em 8 execuções completas**, sempre um único
teste, sempre de integração, nunca o mesmo resultado duas vezes seguidas. Não é
frequente o bastante para bloquear o trabalho, e é frequente o bastante para
enganar quem não souber que existe — daí este parágrafo.

A causa é contenção no **servidor** Firebird, não no driver: o *pooling* já está
desligado em toda conexão (`Pooling=false`, ver `DatabaseService`) e
`FirebirdTestDatabase` já chama `ClearPool`, e o xUnit já roda tudo em série
(`parallelizeTestCollections: false`). O que resta é o próprio Firebird sob
criação/remoção rápida de bancos, com `gbak`/`gfix` no meio.

**Na prática:** falha de conexão ou falha isolada num teste de integração →
rode de novo antes de investigar. **Qualquer falha que se repita é real.**

Como diz o RISCOS-CONHECIDOS: o teste manual de ponta a ponta "vale repetir
antes de qualquer mudança futura no `Worker.cs`/`DatabaseService.cs`" — e é
exatamente esse teste que `WorkerIntegrationTests` automatiza.

### O que o CI roda, e o que é responsabilidade sua

Os testes são divididos por um *trait* do xUnit:

```bash
dotnet test AtualizadorERP.sln --filter "Requer!=Firebird"   # 6 testes, ~2s -- é o que o CI roda
dotnet test AtualizadorERP.sln                               # 39 testes -- exige Firebird 2.5 local
```

O runner do GitHub não tem Firebird 2.5, então **33 dos 39 testes não rodam no
CI**. Eles são responsabilidade de quem desenvolve, antes de abrir o PR.

> Isso não é um detalhe menor. Até setembro de 2026 o workflow compilava e
> publicava a release `latest` — o link que os clientes baixam — **sem rodar um
> teste sequer**. Hoje o job de publicação depende do job de testes, mas a rede
> só pega o que não precisa de banco. **Rode a suíte completa localmente.**

Ao escrever um teste novo que abra conexão com o Firebird, marque a classe (ou
o `[Fact]`) com `[Trait("Requer", "Firebird")]` — senão ele quebra o CI, que
não tem banco para conectar.

## Organização do código

```
Models/      tipos de dados puros: sem comportamento, sem I/O
Services/    tudo que tem comportamento e efeito colateral
Worker.cs    o ciclo e a orquestração das 4 fases
Program.cs   host do serviço Windows + injeção de dependência
```

Um tipo em `Models/` pode ser construído, comparado e serializado sem tocar em
disco, rede ou banco. É isso que permite aos testes montarem cenários sem subir
nada.

Serviços são registrados como *singleton* em `Program.cs` e recebem tudo pelo
construtor — o `Worker` nunca instancia um serviço por conta própria.

## Regras que não se quebram

Estas não são preferências de estilo. Cada uma existe porque a violação já
causou, ou causaria, dano em cliente real:

1. **Todo processo externo passa pelo `ProcessService` e informa um timeout.**
   Sem exceção. Ver [ADR-0003](docs/adr/0003-timeout-obrigatorio.md).
2. **Argumento de processo nunca é montado por interpolação de string.** Vai em
   `ArgumentList`. Credencial vai por variável de ambiente, não por linha de
   comando.
3. **Nada infere "precisa rodar script" a partir do conteúdo do pacote.** Só a
   lista explícita `SISTEMAS_COM_SCRIPT` decide.
   Ver [ADR-0004](docs/adr/0004-lista-explicita-de-sistemas-com-script.md).
4. **Configuração inválida derruba o serviço na subida**, com mensagem dizendo o
   que falta. Nunca deixe um caminho errado aparecer no meio de um ciclo — com o
   banco já em shutdown, é tarde.
5. **Toda fase que para o banco tem que ter um caminho de volta.** Se o código
   pode lançar exceção entre o `gfix -shut` e o `gfix -online`, esse caminho
   precisa reabrir o banco.

## Estilo de código

- Indentação de 4 espaços, `PascalCase` para membros públicos, `_camelCase` para
  campos privados — o padrão do .NET, garantido pelo `.editorconfig`.
- **Nomes de domínio em português** (`ProcessarAtualizacao`, `PastaTrabalho`,
  `SistemasComScript`); vocabulário da plataforma em inglês (`ExecuteAsync`,
  `CancellationToken`). É a convenção já estabelecida no código.
- `Nullable` está **habilitado**. Não desligue para calar um aviso: o aviso está
  apontando um caso real que o agente vai encontrar em campo.
- **Comentário explica *por quê*, não *o quê*.** Este repositório documenta a
  armadilha, não a sintaxe — "usar `Update` e não `Include` aqui, porque
  `Include` cria um segundo item e a limpeza incremental do publish apaga o
  arquivo na segunda publicação". Esse é o padrão, e é o que mais vale aqui.
  Ao corrigir um bug não óbvio, deixe o porquê escrito.

## Publicar

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publicado
```

O `7za.exe` **não** está no repositório (é binário de terceiro) e precisa ficar
ao lado do executável publicado — o `ExtractionService` lança exceção se não o
encontrar. O workflow em `.github/workflows/build.yml` baixa e inclui uma versão
fixa, além de conferir o conteúdo do pacote antes de publicar a release.

## Commits

Imperativo, com escopo quando ajudar. O corpo responde **por que**:

```
fix(worker): reabrir o banco quando a Fase 3 falha antes do gbak
docs(adr): registrar por que a lista de sistemas com script é explícita
```
