# Instruções para agentes de IA neste repositório

> **Leia [`RISCOS-CONHECIDOS.md`](RISCOS-CONHECIDOS.md) antes de alterar
> qualquer coisa.** Não é formalidade. Este programa roda sem supervisão no
> servidor do cliente e pode deixar o banco de dados da empresa dele parado.

Depois, [`CONTRIBUTING.md`](CONTRIBUTING.md). Este arquivo resume só o que é
**não óbvio** e o que mais se erra.

## Verificar antes de dizer que terminou

```bash
dotnet build AtualizadorERP.sln -c Release -warnaserror   # tem que dar 0 warnings
dotnet test  AtualizadorERP.sln                           # 39 testes (exige Firebird 2.5)
```

A suíte tem uma instabilidade conhecida sob carga: um teste de integração
falha de vez em quando e passa na repetição. **Falha que se repete é real.**

## As cinco regras que não se quebram

Cada uma existe porque a violação já causou, ou causaria, dano em cliente real:

1. **Todo processo externo passa pelo `ProcessService` e informa um timeout.**
   Sem exceção. Um processo travado na Fase 3 deixa o banco do cliente em
   shutdown — a empresa inteira dele parada — até alguém perceber.
2. **Argumento de processo nunca é interpolado em string.** Vai em
   `ArgumentList`. Credencial vai por variável de ambiente, nunca na linha de
   comando (qualquer processo local a lê via Gerenciador de Tarefas).
3. **Nada infere "precisa rodar script" pelo conteúdo do pacote.** Só a lista
   explícita `SISTEMAS_COM_SCRIPT`. Pacotes trazem `.sql` por herança de
   empacotamento, e rodá-los **quebra o banco do cliente** — já confirmado.
4. **Configuração inválida derruba o serviço na subida.** Nunca deixe um
   caminho errado só aparecer no meio de um ciclo: depois do `gfix -shut`, é
   tarde.
5. **Toda fase que para o banco tem caminho de volta.** Se algo pode lançar
   entre `gfix -shut` e `gfix -online`, esse caminho precisa reabrir o banco.

## O que NÃO fazer

- **Não troque o SHA1 de `DatabaseService.InjetarNovosBinarios` por SHA256.**
  O campo `HASHEXE` pertence ao schema do ERP Delphi legado. O analisador
  reclama e a supressão está no ponto, com o motivo escrito. Leia antes.
- **Não desligue `Nullable`** para calar um aviso. O aviso aponta um caso real
  que o agente vai encontrar em campo.
- **Não troque comentário "por quê" por comentário "o quê".** Os comentários
  longos aqui registram armadilhas descobertas contra bancos reais. Apagá-los
  apaga o motivo de o código ser assim.
- **Não escreva nomes de domínio em inglês.** `ProcessarAtualizacao`,
  `PastaTrabalho`, `SistemasComScript`. Inglês só onde a plataforma impõe
  (`ExecuteAsync`, `CancellationToken`).

## Organização

```
Models/     tipos de dados puros: sem comportamento, sem I/O
Services/   comportamento e efeito colateral
Worker.cs   o ciclo e a orquestração das 4 fases
Program.cs  host do serviço Windows + injeção de dependência
```

Serviços são singletons registrados em `Program.cs` e recebem tudo pelo
construtor. O `Worker` nunca instancia um serviço por conta própria.

## Testes

Teste novo que abra conexão com Firebird **precisa** de
`[Trait("Requer", "Firebird")]` — senão quebra o CI, que não tem banco.

Testes criam bancos Firebird descartáveis do zero, nunca cópias de produção.

## Ao terminar

Se a decisão é cara de reverter, escreva um ADR em [`docs/adr/`](docs/adr/).
Se descobriu um risco novo, registre em [`RISCOS-CONHECIDOS.md`](RISCOS-CONHECIDOS.md).
Não faça commit nem push sem o usuário pedir.
