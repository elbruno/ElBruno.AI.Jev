# RAG reranking

Assess relevance for three query/passage pairs, preserving candidate IDs and sorting by probability with a deterministic tie-breaker.

```powershell
dotnet run --project samples\10-RagReranking -- --offline
```

Offline fixtures return equal probabilities, intentionally demonstrating stable ordering rather than actual relevance.

For live execution, configure [user-secrets](../../docs/configuration.md) and omit `--offline`. This makes **three billable decision requests**, at most two concurrently, using pinned `jev-1.13.0` by default. No native rerank or embedding endpoint is implied.

Use shortlisting and an application-owned threshold/budget for real workloads. A Noul relevance probability is not a universal similarity metric.
