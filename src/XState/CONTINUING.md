# Tiếp tục port XState

Repo độc lập: đọc [README tại root](../../README.md) và quyết định XSTATE-D15 trước khi chạy lệnh hoặc tích hợp. Source paths hiện dùng src/; bằng chứng migration mới nhất ở đầu PORT-STATUS.

Cập nhật ngày 2026-10-06. Mục tiêu và phạm vi nằm trong [upstream.json](upstream.json); đọc [quyết định kiến trúc](DECISIONS.md) trước khi đổi runtime. [PORT-STATUS.md](PORT-STATUS.md) là nơi ghi kết quả từng đợt; [README của harness](../XState.Tests/README.md) giải thích lệnh và artifacts.

## Repository hiện tại

Làm việc từ root repo xstate-dotnet, với bốn project dưới src/. Khi repo được dùng dưới dạng submodule, commit/push thư viện trước rồi cập nhật commit được pin trong repo chứa nó. Không ghi tiếp vào các thư mục XState cũ của workspace ban đầu. Batch R002 của phiên port đã được nhập đầy đủ theo manifest SHA256; XSTATE-D15 ghi quyết định tách repo và CI.

## Bắt đầu một phiên làm việc

1. Đọc phần trạng thái đầu PORT-STATUS và các lỗi còn mở bên dưới. Kiểm tra source hiện tại vì repo đang có nhiều thay đổi chưa commit; không lấy HEAD hoặc index RepoWise cũ làm bản port hiện hành.
2. Kiểm `tmp/xstate-parity/run-evidence.json`. Nếu status là running, xác định lượt chạy đang hoạt động trước khi dùng cùng output directory. Các runner riêng cũng có thể ghi observations vào đó. Không tự xóa manifest hoặc chạy đè một gate khác.
3. Chọn một nhóm testcase cụ thể từ `parity-report.json`: runtime có upstreamStatus=passed/csharpStatus=pending, pendingTypeAssertionIds cho compiler, hoặc examples có status=pending. Report thiếu/fail/stale không được diễn giải thành 0 pending; chạy lại gate khi workspace đã sẵn sàng.
4. Đọc nguyên testcase upstream, source implementation, bản C# và các caller liên quan. Xác định success/error/cancel/stop/reentrant/restore cần kiểm. Viết expected trace trước khi sửa.
5. Ghi rõ file runtime/test/doc sẽ chạm. Không deploy hoặc tích hợp OhMyBot trong một batch port thư viện.

## XSTATE-R001 WaitFor timeout chạy ngoài actor turn

**Trạng thái tại review 2026-10-06: OPEN, P2.** Source sở hữu lỗi: [ActorTasks.cs](ActorTasks.cs), nhánh tạo System.Threading.Timer trong WaitForAsync.

Probe đã chạy: tạo machine context=0, GO assign context=1. Trong cùng ActorRuntime.Run, đăng ký WaitForAsync với predicate context==1 và timeout 10 ms; giữ lượt đồng bộ 150 ms rồi Send GO trước khi thoát Run. C# trả task Faulted/TimeoutException dù context cuối là 1. Cùng lượt đồng bộ ở JS pin, chờ bận 150 ms rồi send GO: promise resolve. Timer C# gọi Reject trực tiếp trên ThreadPool, không qua execution domain.

**Hướng sửa cần kiểm chứng:** serialize timeout delivery với actor turn và xử lý ownership/disposal đúng lúc. Chọn timer/dispatch contract dựa trên upstream waitFor; không mặc nhiên đổi sang SimulatedClock của actor vì upstream helper dùng timer môi trường. Chỉ tăng timeout hoặc thêm sleep không sửa nguyên nhân.

**Điều kiện đóng:** đưa regression vào bộ resource tests của helper và probe upstream; kiểm timeout thật khi predicate không thỏa, predicate thỏa trong turn, cancellation race, synchronous completion, late callback và thu hồi subscription/timer. Nếu callback mới lấy gate, kiểm lock-order khi dispose timer/registration để tránh deadlock. Lint và bộ test liên quan phải pass sau lượt edit cuối; đo chi phí flow helper trước/sau.

## XSTATE-R002 Initial assign làm mất raw failure

**CLOSED 2026-10-06**, verified run 5fa1a01e-3b24-4165-888a-25d980926091. Regression: InitialErrorTests.cs và tools/reference/initial-errors.test.mjs, 12/12 native + JS pass. Lịch sử finding bên dưới giữ nguyên để truy nguyên. **Tại review ban đầu: OPEN, P2.** Source sở hữu lỗi: [StateMachine.cs](StateMachine.cs), catch trong InitialTransitionCore khi preInitial khác null.

Probe độc lập không cần game:

```csharp
var reason = new Dictionary<string, object?> { ["code"] = 42 };
var machine = new StateMachine<int>(new()
{
    Entry = [MachineActions.Assign<int>((_, _) => throw ActorErrors.ToException(reason))]
}, _ => 0);
var actor = new Actor<MachineSnapshot<int>>(machine);
object? observed = null;
using var subscription = actor.Subscribe(onError: failure => observed = failure);
actor.Start();
// Expected: ReferenceEquals(reason, observed) == true.
// Review: false; observed là carrier RejectionException.
var json = SnapshotJson.Serialize(actor.GetPersistedSnapshot());
// Expected error: {"code":42}; review: {}.
```

Entry Effect đã có test và đi qua deferred-action catch khác; test đó không bao phủ entry Assign được resolve ngay trong initialization. Nhánh có preInitial cần unwrap ActorErrors.GetValue như các boundary khác, vẫn giữ context/value/children của pre-initial snapshot.

**Điều kiện đóng:** bổ sung ErrorValueTests cho initial Assign và output expression với string/number/false/null/object/Exception, kiểm identity trong snapshot/observer/parent onError và JSON round-trip. Giữ nguyên rollback về preInitial khi initialization lỗi; không thay bằng partially resolved context để làm test pass. Đối chiếu JS pin và lint sau edit cuối.

## Bằng chứng của phiên review

Review trước lượt cập nhật tài liệu đã chạy: lint project XState.Tests cùng dependencies 0 warning/error; 1.071 native upstream, 361 supplemental .NET và 624 differential JS pass. Hai probe trên vẫn fail về semantics dù các suite đó pass.

Fuzz dùng seed 76543, 1.000 cấu hình compound/parallel/history, mỗi cấu hình 20 event A/B/C; so state value/status và entry/exit trace giữa native và source pin, không có mismatch. Không có always loop, service integration hoặc bằng chứng toàn bộ async scheduling trong phép fuzz này. Không cộng 20.000 event vào coverage upstream và không suy ra full parity.

Artifact tạm của review ở `tmp/xstate-review/`: Program.cs, reference.test.mjs, fuzz.test.mjs, vectors.json, native-vectors.json, upstream-vectors.json, mismatches.json; kết quả suite ở `tmp/xstate-review-results.json` và `.resources.json`, log reference ở `tmp/xstate-review-reference.log`. Chúng có thể bị dọn. Nội dung tái hiện và tiêu chí đóng lỗi đã lưu ngay trong doc này để công việc không phụ thuộc tmp.

Harness review nằm ngoài dpb-plugins nên lệnh lint:csharp từ chối đường dẫn theo guard của workspace; probe được build Release riêng với warnings-as-errors và analyzer level được chỉ định. Lint pass nêu trên thuộc project test chính. Hai probe chưa được đăng ký thành regression tests của project chính.

## Thứ tự công việc tiếp theo

Ưu tiên sửa R001 và đưa vào gate trước khi tăng coverage; R002 đã đóng. Sau đó chọn nhóm theo report đã verify; ở mốc review ban đầu SCXML có 169 case pending, meta 16, route 13, history 10 và state 9; các batch history/transitions sau đó đã tăng coverage, nên lấy ID còn pending từ report mới thay vì giữ nguyên danh sách cũ. Các file types/setup.types/typeHelpers có runtime cases riêng và compiler groups riêng, không cộng gộp hai loại coverage. Khi chọn batch, không coi danh sách khả năng trong README là danh sách phần chưa có implementation.

- **Runtime:** còn 658 ca chưa port ở run verified 5fa1a01e-3b24-4165-888a-25d980926091, native 1.097/1.755. Mở từng ID pending để xác định đúng contract chưa chứng minh; nhiều API đã có implementation nhưng chưa đủ assertions.
- **Compiler:** 446/453 nhóm còn pending ở cùng mốc. Viết mapping C# rõ ràng cho event unions, registry keys, parameters và snapshot typing; cập nhật positive/negative fixtures cùng manifest. Không thay compiler proof bằng runtime test.
- **Examples:** scope cố định 31 non-UI; run verified 5fa1a01e-3b24-4165-888a-25d980926091 đã pass 8, còn 23 pending. Names/contract có implementation chưa tự được tính pass; cần native behavior, CLI và upstream bằng chứng cùng run. Chọn ví dụ có dependency thật có thể cung cấp và kiểm chứng; thiếu service tiếp tục pending.
- **Semantic gaps đã ghi:** scheduler restore; full Promise.resolve/microtask ordering; arbitrary JSON missing/null/undefined, custom error properties, cycles/containers và malformed input; compile-time API. ResolveState, StateNode definitions, graph và TestModel đã có code, cần đọc test pending thay vì port lại từ đầu.
- **Hiệu năng/lifecycle:** mở rộng stress/reconnect/long-running retention, cold error/serialization, graph nhiều after/metadata/literal input và example layer. Chưa có chứng nhận hiệu năng flow game.

Khi R001 đã được sửa bởi phiên khác, tái hiện trên source mới, cập nhật trạng thái bằng test/result cụ thể và giữ lịch sử finding. Không đóng chỉ vì source đã trông khác hoặc suite cũ vẫn pass.

## Hoàn tất một batch

Đọc [harness README](../XState.Tests/README.md) để chạy đúng pipeline. Một batch cần đủ testcase native với ID gốc, differential JS so toàn bộ assertion/trace liên quan, controls nếu đổi extractor/gate, compiler fixtures nếu đổi contract type, cùng resource/performance checks theo phần bị ảnh hưởng.

Sau mỗi lượt C# edit, chạy lint project bị ảnh hưởng; shared source/analyzer config thì lint toàn bộ theo AGENTS.md. Dừng ở lỗi do thay đổi tạo ra và sửa nguyên nhân; không suppress warning hoặc nới validation. Full gate hiện luôn chạy upstream; strict exit 1 do pending là trạng thái chưa hoàn tất toàn port, không tự là batch regression.

Cập nhật README nếu API/hạn chế đổi, DECISIONS nếu đổi ownership/semantics, phần đầu PORT-STATUS và một mục nhật ký với ngày, scope, counts, runId/artifacts, lint, số đo và giới hạn. Không sao chép số pass của lần chạy khác thành kết quả lần này. Mốc benchmark phải nêu revision/binary hoặc artifacts trước/sau và cùng workload.

Repo này build/test độc lập với RepoWise và workspace ban đầu. Nếu dùng index tùy chọn, refresh sau thay đổi và đối chiếu source khi index cũ. Cấu trúc, CI và quy trình contributor hiện tại ở README/AGENTS.md tại root repo.
