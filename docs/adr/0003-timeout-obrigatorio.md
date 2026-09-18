# ADR-0003 — Timeout obrigatório em todo processo externo

**Situação:** Aceita

## Contexto

O agente executa ferramentas externas: `gfix`, `gbak`, `isql`, `7za.exe` e o
`BScript.exe` do ERP.

A Fase 3 roda com o banco em `gfix -shut multi -force 0`: **terminais
bloqueados, ninguém do cliente consegue trabalhar** até o agente reabrir.

Um risco concreto: não há confirmação de que o `BScript.exe` real aceite ser
chamado de forma verdadeiramente não-interativa. Se ele abrir uma janela dentro
de um serviço do Windows — sessão sem desktop interativo — o processo **nunca
retorna**, e ninguém vê a janela para clicar.

Sem timeout, o resultado é: banco do cliente parado indefinidamente, empresa
inteira sem trabalhar, até alguém perceber e matar o processo à mão no servidor.

## Decisão

Toda execução de processo externo passa por `ProcessService.RunProcessAsync`, e
o parâmetro `timeout` é **obrigatório** — não tem valor padrão, não é opcional.
Esgotado o prazo, o processo é morto com toda a árvore de filhos
(`Kill(entireProcessTree: true)`) e o método lança `TimeoutException` com
mensagem que já aponta a causa provável.

Argumentos vão sempre em `ArgumentList`, nunca numa string montada por
interpolação. Credenciais vão por **variável de ambiente** (`ISC_USER`/
`ISC_PASSWORD`), não por `-user`/`-password` na linha de comando.

## Consequências

**Ganhos**
- O pior caso deixa de ser "parado para sempre" e passa a ser "falhou em N
  minutos, com erro registrado" — e o `Worker` consegue reabrir o banco.
- Não existe caminho no código que escape da regra: quem quiser rodar um
  processo tem que passar por ali e informar um prazo.
- Senha do Firebird não aparece na linha de comando, que é legível por qualquer
  processo local via Gerenciador de Tarefas ou WMI enquanto a ferramenta roda.
- Senha com espaço ou aspas não quebra o parsing, porque nada é concatenado.

**Custos aceitos**
- Cada chamada precisa de um prazo escolhido à mão, e um prazo curto demais mata
  uma operação legítima. Os valores usados (`gfix` 2 min, `gbak` 15 min)
  vieram de medição contra banco real, não de chute.
- Matar a árvore de processos no meio de um `gbak` deixa um arquivo de backup
  pela metade. Aceito: é por isso que o backup é validado antes de a fase
  seguinte prosseguir.

## Alternativas consideradas

- **Timeout com valor padrão** — descartado: um padrão é uma decisão que ninguém
  toma conscientemente. Obrigar o parâmetro força quem escreve a chamada a
  pensar em quanto tempo aquilo deveria levar.
- **Timeout só nas chamadas "arriscadas"** — descartado: exige acertar de
  antemão quais são as arriscadas. A ferramenta que trava é sempre a que ninguém
  achava que ia travar.
