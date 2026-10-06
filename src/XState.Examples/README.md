# XState examples native C#

Port các example không UI của XState 5.33.2 theo [upstream.json](../XState/upstream.json). Mốc verified 2026-10-06, runId `b8d78fde-7a6b-4712-ae80-4bbae4a24cd7`: **8/31 example bắt buộc pass**, **23 pending**, **18 UI excluded**. Giữ MIT license trong thư mục này. Xem [PORT-STATUS.md](../XState/PORT-STATUS.md) cho kết quả hiện hành và [DECISIONS.md](../XState/DECISIONS.md) cho lý do chọn contract.

## Chạy CLI

Từ root workspace:

```powershell
dotnet run --project src/XState.Examples/XState.Examples.csproj -c Release -- workflow-hello
```

Các tên đã có implementation và đã qua execution gate ở mốc trên:

- `workflow-hello`
- `workflow-greeting`
- `workflow-event-greeting`
- `workflow-math-problem`
- `workflow-async-function`
- `workflow-parallel`
- `workflow-async-subflow`
- `workflow-filling-water`

CLI dùng sample input và thứ tự log của entrypoint gốc. Workflow cơ bản giữ delay thật một giây; parallel có nhánh một và ba giây. Onboarding đọc tên và xác nhận từ stdin qua StreamReader do host sở hữu, machine nhận prompt delegate. Email chỉ log gửi giả lập như upstream. Tên chưa port hoặc không tồn tại trả exit code 2.

## Machine và host sở hữu gì

[WorkflowExamples.Create](WorkflowExamples.cs) cung cấp machine, sample input/event. [WorkflowExecution.RunAsync](WorkflowExecution.cs) là flow chung cho CLI và behavior test, thu snapshot/log. Records C# biểu đạt input/event payload; log collector được serialize qua ActorRuntime.Run để hai producer không ghi List đồng thời.

Context traces dùng dữ liệu JSON-compatible: properties undefined của JS bị omit khi so. Root output presence được kiểm riêng; output của final child không tự trở thành root output. Các workflow này không cấu hình root output, nên completion log vẫn là `workflow completed undefined`. Không tự thêm root output để log trông thuận mắt.

Delay/prompt delegate là ranh giới với host. Mặc định delay dùng Task.Delay thật; producer upstream không dùng AbortSignal nên port giữ hành vi producer sau khi actor dừng. Caller hủy việc chờ sẽ stop actor, không chứng minh producer đã ngừng. CLI có deadline 30 giây, EOF báo lỗi transport; các policy này không thay state graph. EOF/cancel ở mọi bước và allocation/GC của lớp example chưa được audit đầy đủ.

Filling-water giữ timer thật 500 ms, trạng thái CheckIfFull/AddWater/GlassFull và counts input. Bốn ca kiểm sample 0/10, đã đầy 10/10, vượt ngưỡng 12/10 và số thập phân 0.5/2 (kết thúc ở 2.5). Không clamp input/assignment. Hook Observe của example in state/context tại từng snapshot. Object log của JS được serialize từ arguments để so JSON; native CLI in JSON cho object, khác cách Node inspect object trên terminal.

## Gate thực thi bắt buộc

```powershell
pwsh -NoProfile -File src/XState.Tests/tools/Test-Parity.ps1 -RequireComplete
```

Lệnh luôn chạy suite JS core gốc, lint/build, native core, JS/C# differential, example behavior, CLI, upstream examples và compiler. Native example behavior chạy bằng project XState.Examples.Tests riêng; core runner không reference examples. Setup, lệnh chạy tập trung, output và cách đọc exit code nằm trong [README của harness](../XState.Tests/README.md).

Example runner import entrypoint upstream không sửa source, alias xstate về core pin, rồi so state/context snapshots, output presence, completion count và logs. Case phụ gồm input khác, math batch rỗng và đảo thứ tự hoàn tất hai nhánh parallel. Native/upstream evidence phải cùng example runId và source digest. Build-only, skipped, thiếu assertion hoặc thiếu CLI execution không được tính pass.

Onboarding còn chạy process JS thật với stdin/stdout pipes. Driver chờ đúng prompt rồi mới gửi từng câu trả lời; không bơm toàn bộ input trước vì Node readline có thể bỏ câu trả lời chưa có question chờ. Không đóng stdin sớm để ép process thoát. Snapshot comparison của entrypoint script điều khiển readline transport và quan sát actor do entrypoint gốc tạo; process probe riêng chứng minh I/O thật. Prompt native xuống dòng còn readline upstream không xuống dòng; mỗi transcript được kiểm theo đúng format của host đó.

Mốc verified trên có **17 native + 17 upstream behavior checks**, **8 native CLI executions** và real upstream onboarding CLI pass. Manifest whole-pipeline và artifact hashes nằm trong run-evidence.json; reporter từ chối evidence thiếu/stale. Strict gate vẫn exit 1 vì core/compiler và 23 example còn pending.

## Thêm một example

1. Xác nhận example thuộc required scope trong [examples-inventory.json](../XState.Tests/examples-inventory.json), đọc entrypoint và dependency gốc. Console tương tác vẫn là non-UI; chỉ loại theo danh sách UI đã được user chọn.
2. Port machine/flow native vào project này và đăng ký tên CLI. Giữ timer, input/output, event ordering và error/cancel behavior; host transport được inject ở ranh giới cần thiết. Dùng runtime XState chung, không nhúng JS để thực thi thay.
3. Đăng ký named behavior checks trong [tools/examples.mjs](../XState.Tests/tools/examples.mjs), thêm native checks trong [ExampleTests.cs](../XState.Examples.Tests/ExampleTests.cs), rồi upstream checks dưới [tools/examples](../XState.Tests/tools/examples). So toàn bộ dữ liệu cần thiết, không bỏ field để làm hai bên khớp.
4. Bảo đảm [Run-ExampleCli.mjs](../XState.Tests/tools/Run-ExampleCli.mjs) thực sự chạy entrypoint; console dialogue dùng [run-example-process.mjs](../XState.Tests/tools/run-example-process.mjs), có timeout/output cap và transcript assertions. Example MongoDB/HTTP cần service integration thật trước khi pass; thiếu service vẫn pending.
5. Lint sau mỗi lượt edit C#, chạy gate khi không có lượt khác đang ghi artifacts, cập nhật số verified trong PORT-STATUS và doc này. Thêm controls nếu mở rộng execution/evidence contract. Functional pass chưa chứng minh memory/performance parity; lưu rõ phần chưa đo.
