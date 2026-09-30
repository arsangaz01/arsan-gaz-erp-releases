# Projects and dependencies analysis

This document provides a comprehensive overview of the projects and their dependencies in the context of upgrading to .NETCoreApp,Version=v10.0.

Detailed findings live alongside this file in `assessment/`. This page is the index: read it first, then open only the documents you need.

## Table of Contents

- [Executive Summary](#executive-summary)
  - [Highlevel Metrics](#highlevel-metrics)
  - [Projects Compatibility](#projects-compatibility)
  - [Package Compatibility](#package-compatibility)
  - [API Compatibility](#api-compatibility)
- [Top API Migration Challenges](#top-api-migration-challenges)
  - [Technologies and Features](#technologies-and-features)
  - [Most Frequent API Issues](#most-frequent-api-issues)
- [Detailed Reports](#detailed-reports)
  - [Projects Relationship Graph](assessment/project-graph.md)
  - [Aggregate NuGet packages details](assessment/nuget/aggregate-packages.md)
  - [Most Frequent API Issues (complete list)](assessment/api-issues/most-frequent-api-issues.md)
  - [Project Details](#project-details)

## Executive Summary

### Highlevel Metrics

| Metric | Count | Status |
| :--- | :---: | :--- |
| Total Projects | 1 | All require upgrade |
| Total NuGet Packages | 51 | 3 need upgrade |
| Total Code Files | 29 |  |
| Total Code Files with Incidents | 11 |  |
| Total Lines of Code | 2101 |  |
| Total Number of Issues | 338 |  |
| Proposed Target Framework | net10.0-windows |  |
| Estimated LOC to modify | 334+ | at least 15,9% of codebase |

### Projects Compatibility

| Project | Target Framework | Difficulty | Test Coverage | Package Issues | API Issues | Binding Issues | Est. LOC Impact | Description |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| [ArsanGazERP.csproj](assessment/projects/ArsanGazERP.md) | net8.0-windows | 🟡 Medium | 🧪 Recommended | 3 | 334 | 0 | 334+ | Wpf, Sdk Style = True |

🧪 **Test Coverage** — projects risky enough to add behavior-locking tests before upgrading, to catch regressions the upgrade may introduce. Requires the **dotnet-test** plugin.

### Package Compatibility

| Status | Count | Percentage |
| :--- | :---: | :---: |
| ✅ Compatible | 48 | 94,1% |
| ⚠️ Incompatible | 0 | 0,0% |
| 🔄 Upgrade Recommended | 3 | 5,9% |
| ***Total NuGet Packages*** | ***51*** | ***100%*** |

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 317 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 17 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 4538 |  |
| ***Total APIs Analyzed*** | ***4872*** |  |

## Top API Migration Challenges

### Technologies and Features

| Technology | Issues | Percentage | Migration Path |
| :--- | :---: | :---: | :--- |
| WPF (Windows Presentation Foundation) | 159 | 47,6% | WPF APIs for building Windows desktop applications with XAML-based UI that are available in .NET on Windows. WPF provides rich desktop UI capabilities with data binding and styling. Enable Windows Desktop support: Option 1 (Recommended): Target net9.0-windows; Option 2: Add <UseWindowsDesktop>true</UseWindowsDesktop>. |

### Most Frequent API Issues

| API | Count | Percentage | Category |
| :--- | :---: | :---: | :--- |
| T:System.Windows.RoutedEventHandler | 58 | 17,4% | Binary Incompatible |
| T:System.Windows.Controls.TextBox | 49 | 14,7% | Binary Incompatible |
| T:System.Windows.Controls.DataGrid | 33 | 9,9% | Binary Incompatible |
| E:System.Windows.Controls.Primitives.ButtonBase.Click | 25 | 7,5% | Binary Incompatible |
| T:System.Windows.RoutedEventArgs | 25 | 7,5% | Binary Incompatible |
| P:System.Windows.Controls.TextBox.Text | 23 | 6,9% | Binary Incompatible |
| P:System.Windows.Controls.ItemsControl.ItemsSource | 10 | 3,0% | Binary Incompatible |
| T:System.Windows.MessageBoxResult | 10 | 3,0% | Binary Incompatible |
| M:System.Windows.Window.#ctor | 6 | 1,8% | Binary Incompatible |
| T:System.Uri | 6 | 1,8% | Behavioral Change |

The table above is the top 10. See [the complete list](assessment/api-issues/most-frequent-api-issues.md) for every affected API.

## Detailed Reports

- [Projects Relationship Graph](assessment/project-graph.md)
- [Aggregate NuGet packages details](assessment/nuget/aggregate-packages.md)
- [Most Frequent API Issues (complete list)](assessment/api-issues/most-frequent-api-issues.md)

### Project Details

- [ArsanGazERP.csproj](assessment/projects/ArsanGazERP.md)


