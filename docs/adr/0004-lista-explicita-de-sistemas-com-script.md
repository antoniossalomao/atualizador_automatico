# ADR-0004 — Lista explícita de sistemas com script

**Situação:** Aceita

## Contexto

Alguns produtos do ERP precisam rodar scripts `.sql` contra o `JUNIOR.fdb` ao
atualizar (alterar tabela, criar procedure). Outros só precisam ter o
executável trocado.

A diferença é crítica: o caminho com script passa por parar o banco
(`gfix -shut`), fazer backup, rodar `isql` arquivo por arquivo e reabrir. O
caminho sem script copia arquivos e pronto, **sem interromper ninguém**.

O jeito aparentemente óbvio de decidir seria olhar o pacote baixado: *"tem
`.sql` dentro? então roda os scripts"*.

Isso está **errado**, e foi confirmado na prática: pacotes como o do
`BImportaXML` trazem `.sql` junto por herança de empacotamento — arquivos que
sobraram de builds antigos. Executá-los contra o banco daquele cliente **quebra
o banco**.

## Decisão

Uma chave de configuração explícita, `SISTEMAS_COM_SCRIPT`, lista quais
sistemas têm permissão de rodar script. Todo o resto é tratado como "só troca de
executável" — **nunca** passa pelo `ScriptRunnerService`, mesmo que o pacote
baixado contenha `.sql`.

Há também `SCRIPTS_IGNORADOS`: nomes de arquivo que o executor nunca tenta
rodar, para scripts legados quebrados de origem (por exemplo, um corpo de
procedure salvo sem o cabeçalho `ALTER PROCEDURE` — encontrado inspecionando os
scripts reais do B_Vendas). Comparado pelo nome do arquivo, que é a mesma chave
que a tabela `SCRIPTS` já usa.

## Consequências

**Ganhos**
- Um pacote com `.sql` acidental não causa dano nenhum num cliente que não
  declarou aquele sistema como "com script".
- A decisão mais perigosa do agente é uma linha de texto que um humano escreveu
  de propósito, auditável no `.ini`, não uma inferência do programa.
- O padrão é o **seguro**: um sistema novo, esquecido na configuração, nasce
  sem permissão de tocar no banco.

**Custos aceitos**
- É configuração manual que pode ser esquecida. O sintoma do esquecimento,
  porém, é benigno: o sistema atualiza só o executável e o script não roda —
  visível e corrigível. O erro no sentido contrário é que seria destrutivo.
- `SCRIPTS_IGNORADOS` esconde um problema real (um arquivo quebrado na origem)
  em vez de corrigi-lo. Aceito porque a correção não está sob nosso controle:
  o arquivo veio incompleto e nenhuma correção automática resolve.

## Alternativas consideradas

- **Inferir pelo conteúdo do pacote** — descartado: comprovadamente errado (ver
  contexto). É o caso clássico em que a heurística conveniente é destrutiva.
- **A API central informar se a versão tem script** — descartado por ora, mas é
  a evolução natural: quem publica a versão sabe se ela tem script, e isso
  eliminaria a configuração por cliente. Exigiria acrescentar o campo ao
  contrato da API e garantir que nenhum agente antigo interprete a ausência
  dele como "tem script". Enquanto isso não existir, a lista explícita local é
  a opção segura.
