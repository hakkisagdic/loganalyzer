---
type: "query"
date: "2026-09-26T15:15:48.158450+00:00"
question: "Which product discovery, architecture and wiki digest guards are affected by OTLP fixture integration?"
contributor: "graphify"
outcome: "useful"
source_nodes: ["ProductDiscovery", "ArchitectureTests", "CompositionRootTests", "WikiSourceDigestStamper"]
---

# Q: Which product discovery, architecture and wiki digest guards are affected by OTLP fixture integration?

## Answer

Expanded from graph vocabulary: product discovery fixture composition registration architecture telemetry wiki digest stamp. Traversal identified ProductDiscovery, ArchitectureTests, CompositionRootTests, WikiSourceDigestStamper. GitHub CI evidence established that the fixture assembly must be loadable in UnitTests, AddBizigoTelemetry must be in the architecture expected registrar set, and README-linked wiki source digests must be restamped.

## Outcome

- Signal: useful

## Source Nodes

- ProductDiscovery
- ArchitectureTests
- CompositionRootTests
- WikiSourceDigestStamper