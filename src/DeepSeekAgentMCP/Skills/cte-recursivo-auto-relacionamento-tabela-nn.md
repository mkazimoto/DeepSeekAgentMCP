# Skill: Consulta com CTE Recursivo (Auto-Relacionamento) com uma tabela n para n que referencia a mesma tabela

## Quando usar

Use esta skill quando o usuário precisar de uma consulta SQL com CTE recursivo para tabelas n para n que possuem auto-relacionamento para a mesma tabela (as 2 colunas referencia a mesma tabela).

## Exemplo prático (MRECCMP)

Tabela n para n com auto-relacionamento para a tabela MCMP: `IDCMPFILHA` → `IDCMP` (ambos na mesma tabela `MCMP`).
Pedido: Listar toda a hierarquia de recursos da tarefas da tarefa 122 do projeto 2 e coligada 1.

```sql server
WITH CTE_RECURSIVO AS (
    -- ÂNCORA: composição raiz (ponto de partida)
    SELECT
        R.CODCOLIGADA,
        R.IDPRJ,
        R.IDREC,
        R.IDPRJREC,
        R.IDCMP,                                          -- Composição pai
        R.IDCMPFILHA,                                     -- Sub-composição filha
        R.IDISM,
        R.QUANTIDADE,
        R.VALORUNIT,
        R.VALORTOTAL,
        R.ATIVO,
        0 AS NIVEL          
    FROM MTAREFA T (NOLOCK)
         INNER JOIN MRECCMP R (NOLOCK) 
           ON R.CODCOLIGADA = T.CODCOLIGADA 
          AND R.IDPRJ = T.IDPRJREC 
          AND R.IDCMP = T.IDCMP
         INNER JOIN MCMP C (NOLOCK)
           ON C.CODCOLIGADA = R.CODCOLIGADA 
          AND C.IDPRJ = R.IDPRJREC 
          AND C.IDCMP = R.IDCMP
    WHERE
        T.CODCOLIGADA = 1                            
    AND T.IDPRJ = 2                              
    AND T.IDTRF = 122       -- Id da tarefa                    

    UNION ALL

    -- PARTE RECURSIVA: sub-composições 
    SELECT
        F.CODCOLIGADA,
        F.IDPRJ,
        F.IDREC,
        F.IDPRJREC,
        F.IDCMP,
        F.IDCMPFILHA,
        F.IDISM,
        F.QUANTIDADE,
        F.VALORUNIT,
        F.VALORTOTAL,
        F.ATIVO,
        P.NIVEL + 1
    FROM MRECCMP F (NOLOCK)       
         INNER JOIN CTE_RECURSIVO P
            ON  P.CODCOLIGADA = F.CODCOLIGADA
            AND P.IDPRJ       = F.IDPRJ
            AND P.IDCMPFILHA  = F.IDCMP         -- A filha vira a composição pai no nível abaixo
    WHERE
        P.NIVEL < 20  -- Proteção contra recursão infinita
)
SELECT
    H.CODCOLIGADA,
    H.IDPRJ,
    H.IDREC,
    H.IDPRJREC,
    H.IDCMP,       
    H.IDCMPFILHA, 
    H.IDISM,      
    COALESCE(H.IDCMPFILHA, H.IDCMP) IDPAI,
    COALESCE(I.CODISM, C.CODCMP) CODRECURSO,
    COALESCE(I.DESCISM, C.DESCCMP) DSCRECURSO,
    H.QUANTIDADE,
    H.ATIVO,
    H.VALORUNIT,
    H.VALORTOTAL,
    H.NIVEL
FROM CTE_RECURSIVO H 
     LEFT JOIN MCMP C (NOLOCK)
       ON C.CODCOLIGADA = H.CODCOLIGADA
      AND C.IDPRJ = H.IDPRJREC
      AND C.IDCMP = H.IDCMP
     LEFT JOIN MISM I (NOLOCK)
       ON I.CODCOLIGADA = H.CODCOLIGADA
      AND I.IDPRJ = H.IDPRJREC
      AND I.IDISM = H.IDISM
ORDER BY
  H.NIVEL, 
  H.IDCMPFILHA
OPTION (MAXRECURSION 20)
```


