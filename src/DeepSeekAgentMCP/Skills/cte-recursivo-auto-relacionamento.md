# Skill: Consulta com CTE Recursivo (Auto-Relacionamento)

## Quando usar

Use esta skill quando o usuário precisar de uma consulta SQL com CTE recursivo para tabelas que possuem auto-relacionamento (uma coluna que referencia a própria tabela, como `IDPAI → ID`).

## Exemplo prático (MTAREFA)

Tabela com auto-relacionamento: `IDPAI` → `IDTRF` (ambos na mesma tabela `MTAREFA`).

Pedido: Listar a hierarquia de todas as tarefas da tarefa 126 do projeto 2 e coligada 1.

```sql server
WITH CTE_RECURSIVO AS (
    -- Âncora: a própria tarefa raiz (opcional, ou pode começar só pelos filhos)
    SELECT
        T.CODCOLIGADA,
        T.IDPRJ,
        T.IDTRF,
        T.CODTRF,
        T.NOME,
        T.IDPAI,
        0 AS NIVEL
    FROM 
        MTAREFA T (NOLOCK)
    WHERE T.CODCOLIGADA = 1
      AND T.IDPRJ = 2
      AND T.IDTRF = 126    

    UNION ALL

    -- Passo recursivo: busca os filhos de cada tarefa encontrada
    SELECT
        T.CODCOLIGADA,
        T.IDPRJ,
        T.IDTRF,
        T.CODTRF,
        T.NOME,
        T.IDPAI,
        C.NIVEL + 1 AS NIVEL
    FROM
          MTAREFA T (NOLOCK)
    INNER JOIN CTE_RECURSIVO C
        ON T.CODCOLIGADA = C.CODCOLIGADA
       AND T.IDPRJ = C.IDPRJ
       AND T.IDPAI = C.IDTRF
       AND T.IDPAI <> T.IDTRF -- a tarefa raiz tem os 2 campos iguais
    WHERE
        C.NIVEL < 20 -- Evita recursão infinita
)
SELECT
    C.CODCOLIGADA,
    C.IDPRJ,
    C.IDTRF,
    C.CODTRF,
    C.NOME,
    C.IDPAI,
    C.NIVEL
FROM 
    CTE_RECURSIVO C
ORDER BY 
    C.CODTRF
OPTION (MAXRECURSION 20)
```

## Regras importantes

1. **Evitar OUTER JOIN no CTE RECURSIVO** utilize apenas LEFT, RIGHT, FULL OUTER JOIN no SELECT principal
2. **`t.IDPAI <> t.IDTRF`** é necessário para evitar recursão infinita
3. **`c.NIVEL < 20`** é necessário para evitar recursão infinita
4. **`OPTION (MAXRECURSION 20)`** é necessário para evitar recursão infinita
5. **Sempre use `UNION ALL`** entre âncora e parte recursiva
6. **Coluna `NIVEL`** obrigatória para rastrear profundidade (incrementar com `+ 1`)
7. **Chaves compostas**: quando a PK for composta, inclua TODAS as colunas no JOIN recursivo
8. **`(NOLOCK)`** recomendado para consultas de leitura em produção
9. **ORDER BY** por código hierárquico para preservar a estrutura de árvore
