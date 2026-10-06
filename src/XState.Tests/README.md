# Kiểm thử bản port XState

Đọc [CONTINUING.md](../XState/CONTINUING.md) để chọn việc tiếp theo và [DECISIONS.md](../XState/DECISIONS.md) để hiểu hợp đồng cần giữ. Project là console test runner .NET 8; chạy DLL hoặc Test-Parity.ps1, không dùng kết quả `dotnet test` rỗng làm bằng chứng.

## Chuẩn bị và chạy đầy đủ

Chạy từ root workspace với PowerShell 7, .NET SDK 8 và Bun. Setup tải source archive/Node đã pin, kiểm SHA256 và cài dependency theo tools/package-lock.json vào data/library-source. Runtime library không có dependency Node.

```powershell
pwsh -NoProfile -File src/XState.Tests/tools/Setup-Upstream.ps1
pwsh -NoProfile -File src/XState.Tests/tools/Test-Parity.ps1
pwsh -NoProfile -File src/XState.Tests/tools/Test-Parity.ps1 -RequireComplete
```

Hai lệnh test cuối là hai lựa chọn, không cần chạy liên tiếp nếu chỉ cần strict gate. Runner hiện chỉ nhận `-RequireComplete`; `-RunUpstream` đã bị bỏ vì upstream baseline nay luôn chạy.

`Test-Parity.ps1` chạy source verification và controls, sinh inventory/fixtures, upstream baseline, lint/build, native runtime/resource checks, JS/C# differential, native examples, CLI, upstream examples và compiler fixtures. Nó dùng Node theo .node-version của source pin (mốc hiện tại 24.16.0), không dựa vào node trên PATH.

Không chạy đồng thời hai gate hoặc lệnh riêng ghi vào tmp/xstate-parity. Kiểm run-evidence.json trước; runner không có cơ chế cô lập output cho từng process song song. Một run mới xóa parity-report cũ, capture hash của sáu stage rồi mới verify và sinh report.

## Diễn giải kết quả

- Gate thường exit 0 nghĩa tập đã chạy qua các bước kiểm tra, chưa chứng minh toàn bộ port complete.
- Strict gate exit 1 và report `complete:false` có thể chỉ do runtime/compiler/examples pending. Xem counts và logs để phân biệt thiếu coverage với failure thật.
- Source mismatch, failed stage, missing/stale artifacts, compiler diagnostic sai, example thiếu CLI hoặc sai runId đều không được bỏ qua.
- Manifest `status:verified` xác nhận các execution stage/artifact của lượt đó; không đồng nghĩa `complete:true`. Manifest còn running/failed hoặc report không tồn tại không phải kết quả pass.

`run-evidence.json` và `parity-report.json` có runId của gate. Example evidence có runId riêng dùng để ghép native/upstream examples; không yêu cầu nó bằng gate runId. Compiler evidence có fingerprint DLL/compiler/harness/fixtures riêng. Đừng ghép report thủ công từ các run.

## Các loại coverage

AST inventory đếm khai báo test, còn baseline Vitest đếm runtime case sau khi expand each/loops. Mốc review 2026-10-06: 1.455 declarations, 1.755 upstream runtime pass, 13 skip và 1 todo; native đã port 1.071 case, compiler 7/453 nhóm. Đây là mốc lịch sử, số hiện hành đọc từ report đã verify.

Native test ID phải khớp `path::ancestor > title`, giữ occurrence suffix nếu tên trùng. Supplemental .NET tests phục vụ resource/concurrency riêng. Differential JS đối chiếu native observations với source pin. Examples có scope và behavior contracts riêng. Không cộng các số này thành một tỷ lệ parity chung.

## Lệnh tập trung khi phát triển

```powershell
bun run lint:csharp -Project src/XState.Tests/XState.Tests.csproj
dotnet tmp/csharp-lint/XState.Tests/XState.Tests/XState.Tests.dll tmp/xstate-parity/csharp-results.json
& data/library-source/node-v24.16.0-win-x64/node.exe data/library-source/xstate-test-tools/node_modules/vitest/vitest.mjs run --config src/XState.Tests/tools/reference.vitest.mjs
```

Native runner sinh thêm observations csharp-*.json mà các JS probes cần đọc; chạy native trước differential. Các lệnh riêng không tạo manifest full gate mới và có thể làm artifact hashes của run cũ hết hiệu lực. Sau khi kết thúc batch, chạy gate để tạo evidence thống nhất. Nếu pin Node đổi, dùng node.exe theo source .node-version thay vì copy nguyên đường dẫn phiên cũ.

Example behavior có runner riêng trong XState.Examples.Tests; core runner không nhận mode `--examples` và không reference project examples. Khi sửa example, lint/build và chạy runner riêng trước CLI/upstream comparison:

```powershell
bun run lint:csharp -Project src/XState.Examples.Tests/XState.Examples.Tests.csproj
dotnet tmp/csharp-lint/XState.Examples.Tests/XState.Examples.Tests/XState.Examples.Tests.dll tmp/xstate-parity/csharp-examples.json
& data/library-source/node-v24.16.0-win-x64/node.exe src/XState.Tests/tools/Run-ExampleCli.mjs
& data/library-source/node-v24.16.0-win-x64/node.exe data/library-source/xstate-test-tools/node_modules/vitest/vitest.mjs run --config src/XState.Tests/tools/examples.vitest.mjs
```

Các mode benchmark nằm trong Program.cs, ví dụ `--benchmark`, `--benchmark-invoke`, `--benchmark-machine-creation`, `--benchmark-persistence-json`. Mỗi mode nhận output JSON path làm argument sau tên mode. Dùng binary Release với cùng JIT/tiered settings, warmup/workload và samples cho cả trước/sau; lưu revision/artifact và host-load caveat. Benchmark không thay correctness test.

## File nào sở hữu việc gì

- [Program.cs](Program.cs): đăng ký và chạy native upstream/resource cases, export observations và chọn benchmark modes.
- [upstream-inventory.json](upstream-inventory.json), [tools/inventory.mjs](tools/inventory.mjs): declarations, hash và type-assertion classification.
- [tools/translate-data-tests.mjs](tools/translate-data-tests.mjs) và extractors parallel/graph-paths/transition-tables: chuyển nguyên cấu hình/assertions hỗ trợ được; controls phải từ chối phần thiếu semantics.
- [tools/reference.vitest.mjs](tools/reference.vitest.mjs), [tools/reference](tools/reference): differential runtime suite trên source pin.
- [compiler-cases/manifest.json](compiler-cases/manifest.json), [tools/Compile-Contracts.mjs](tools/Compile-Contracts.mjs): positive/negative C# fixtures và diagnostic expectations. Fixtures .cs.txt không được compile vào runner thường.
- [tools/examples.mjs](tools/examples.mjs), [ExampleTests.cs](../XState.Examples.Tests/ExampleTests.cs), [tools/Run-ExampleCli.mjs](tools/Run-ExampleCli.mjs), [tools/examples](tools/examples): example scope/contracts, native behavior trong project riêng, process execution và upstream comparison.
- [tools/run-evidence.mjs](tools/run-evidence.mjs), [tools/parity-report.mjs](tools/parity-report.mjs): evidence lifecycle và completion report.

## Artifacts và phục hồi

Output tái tạo nằm dưới tmp/xstate-parity: upstream-source-verification.json; upstream-results.json; csharp-results.json và .resources.json; reference-results.json; csharp-examples.json, upstream-examples.json, example-runner-results.json; compile-results.json; run-evidence.json và parity-report.json. Log chính: upstream-run.log, reference-run.log, examples-reference.log. Binary lint nằm dưới tmp/csharp-lint.

Source archive/dependency tải về nằm ở data/library-source; fixture/inventory/compiler manifest có owner trong project test. Không sửa generated expected hoặc cache để hợp thức hóa failure. Setup chỉ giải nén source khi chưa có thư mục; nếu verification phát hiện source pin bị sửa, điều tra và phục hồi từ archive đã verify trước khi chạy lại.

Khi thêm test, cập nhật registration/export/reference/manifest liên quan cùng một batch. Khi đổi harness, thêm control tests cho false-positive mà thay đổi cần chặn. Các bug review còn mở và cách tái hiện nằm ở [CONTINUING.md](../XState/CONTINUING.md).
