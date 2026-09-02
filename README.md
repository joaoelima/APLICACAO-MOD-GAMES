# Game Trainer

Protótipo de trainer modular para jogos single player/offline no Windows.

## Primeiro jogo

Crimson Desert.

## Objetivo da v0.1

- detectar automaticamente o processo do Crimson Desert;
- conectar ao processo com APIs nativas do Windows;
- carregar uma definição dinâmica de recursos do jogo;
- preparar leitura/escrita de memória;
- preparar suporte a assinaturas AOB e patches por versão;
- interface leve em WPF/.NET 8.

## Estrutura

- `GameTrainer.App`: interface desktop WPF;
- `GameTrainer.Core`: detecção de processo, memória e contratos dos módulos;
- `GameTrainer.Modules.CrimsonDesert`: definição e implementação específica do Crimson Desert.

## Estado atual

A base funcional está pronta para detectar e anexar ao processo. As modificações reais do Crimson Desert ainda precisam das assinaturas/offsets confirmados para a versão instalada do jogo.

## Requisitos

- Windows 10/11 x64;
- .NET 8 SDK;
- Visual Studio 2022 opcional.

## Execução

```powershell
dotnet build GameTrainer.sln
dotnet run --project src/GameTrainer.App/GameTrainer.App.csproj
```

> Escopo: jogos offline/single player, sem contorno de anti-cheat ou proteções de multiplayer.
