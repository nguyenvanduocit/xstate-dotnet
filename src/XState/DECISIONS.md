# Quyết định khi port XState sang .NET

Bản ghi tổng hợp ngày 2026-10-06 từ source và nhật ký port, áp dụng cho XState 5.33.2, commit `fbee62e7c1586315ed478c2fedf530d7e0ff5a3e`. Đây là cách triển khai hiện tại và lý do đã được ghi nhận; không phải chứng nhận full parity. Bắt đầu công việc ở [CONTINUING.md](CONTINUING.md); trạng thái và số đo theo từng đợt nằm trong [PORT-STATUS.md](PORT-STATUS.md).

Khi đổi một quyết định, cập nhật mục tương ứng, nêu quyết định nào được thay thế, thêm regression/differential test rồi ghi kết quả vào nhật ký. Không suy ra yêu cầu mới chỉ từ một test đã pass.

## XSTATE-D01 Phạm vi và nguồn đối chiếu

**Quyết định:** port native toàn bộ package core, gồm `xstate/graph`, test runtime, hợp đồng compile-time tương ứng và examples không UI. Giữ MIT license. Nguồn đối chiếu cố định trong [upstream.json](upstream.json), không lấy npm latest hoặc bản khác để làm oracle.

User đã yêu cầu examples phải thực sự chạy như integration tests, sau đó loại examples có UI tương tác. Có 49 example, 31 bắt buộc và 18 excluded-ui; exclusion phải có tên, lý do và đường dẫn source chứng minh. Adapter UI/package store riêng nằm ngoài phạm vi. Example console hỏi đáp vẫn thuộc phạm vi.

**Lý do và hệ quả:** pin commit giúp phân biệt lỗi port với thay đổi upstream. Hash archive và từng source file phải khớp trước test; không sửa expected/source pin để làm bản port pass. Test skip/todo và example bị loại không được cộng vào pass. Căn cứ: [Verify-Upstream.ps1](../XState.Tests/tools/Verify-Upstream.ps1), [examples.mjs](../XState.Tests/tools/examples.mjs), lịch sử phạm vi trong PORT-STATUS.

## XSTATE-D02 Runtime native độc lập với host

**Quyết định:** [XState.csproj](XState.csproj) target `net8.0`, không phụ thuộc DPB, UI hoặc runtime JavaScript. Node/Vitest chỉ ở harness đối chiếu. `JsonSchema.Net` nằm trong project test và được khóa phiên bản; runtime không tự validate JSON schema.

**Lý do và hệ quả:** statechart và actor phải test được không cần game. Chưa tích hợp OhMyBot; port xong không tự có nghĩa đã verify lifecycle trong host. Tích hợp vào Brain là công việc riêng, cần impact review và live-test theo luật workspace. Không tự thêm bridge JS để lấp phần chưa port.

## XSTATE-D03 Snapshot và actor logic là hai contract riêng

**Quyết định:** `Actor<TSnapshot>` chạy `IActorLogic<TSnapshot>`; machine là một logic tạo `MachineSnapshot<TContext>`. `IActor` và `ActorSource` là ranh giới cho cây actor có nhiều kiểu snapshot. Graph/config thuộc machine; mailbox, observer, deferred effects và lifecycle thuộc actor.

**Lý do và hệ quả:** cùng actor lifecycle phục vụ machine, reducer, promise, observable và callback. Caller phải kiểm kiểu snapshot khi đi qua IActor. Không gắn actor trở lại riêng với TContext hoặc nhúng state machine thứ hai vào host. Căn cứ: [Actor.cs](Actor.cs), [ActorLogic.cs](ActorLogic.cs), [Invocation.cs](Invocation.cs), [ActorSpawner.cs](ActorSpawner.cs).

## XSTATE-D04 Một execution domain đồng bộ và reentrant

**Quyết định:** mọi root dùng chung `ActorRuntime.Gate`; `ActorRuntime.Run` gom nhiều thao tác đồng bộ thành một lượt. Mailbox xử lý FIFO; reentrant send được xếp hàng. Runtime không tạo thread riêng. RealClock dùng hàng đợi deadline chung theo SynchronizationContext, hoặc ThreadPool khi không có context.

**Lý do:** lock riêng theo actor/root có thể deadlock khi hai root gửi chéo; JS gốc chỉ có một luồng thực thi đồng bộ. Gate chung đổi parallel throughput lấy thứ tự và tránh lock-order deadlock.

**Hệ quả:** không await hoặc chờ một turn khác bên trong Run/action callback. Context được capture không biến mọi lời gọi Send từ thread bất kỳ thành lời gọi trên UI thread. Host cần affinity phải điều phối caller. SimulatedClock chạy callback trên thread Increment/Set. Timer ngoài execution domain không được mặc nhiên coi là có cùng ordering: lỗi WaitFor hiện được ghi ở XSTATE-R001 trong [CONTINUING.md](CONTINUING.md).

Căn cứ trực tiếp: marker DECISION trong [ActorRuntime.cs](ActorRuntime.cs), [Mailbox.cs](Mailbox.cs), [RealClock.cs](RealClock.cs), nhật ký clock thực.

## XSTATE-D05 Promise jobs và ownership khi hủy

**Quyết định:** Task và callable thenable dùng `ActorMicrotasks` để giao reaction sau lượt đồng bộ; `ActorRuntime.YieldAsync` cung cấp checkpoint. Mỗi invocation có CTS; Stop hủy signal, bỏ delivery đang chờ và tách reference nội bộ tới actor. CTS chỉ được dispose khi producer đã kết thúc; không nhận late completion.

Getter ThenHandler được đọc ngay; gọi then là một promise job. Chỉ settlement đầu có hiệu lực, Adopt khóa settlement trong lúc nhận thenable khác. Stop không xóa producer job mà upstream vẫn chạy.

**Trade-off:** cancel actor không chứng minh Task/producer đã dừng. Chưa mô phỏng toàn bộ Promise.resolve, JS microtask/event-loop ordering hoặc non-callable then. Không đổi sang continuation chạy ngay chỉ để giảm latency. Căn cứ: [PromiseLogic.cs](PromiseLogic.cs), [PromiseLike.cs](PromiseLike.cs), [ActorMicrotasks.cs](ActorMicrotasks.cs), PromiseLogicTests/ThenableTests và reference probes.

## XSTATE-D06 Giữ hành vi upstream dù dễ tưởng là lỗi

**Quyết định:** giữ thứ tự entry → invoke → initial actions, resolution của assign trước effect, send bind vào actor instance, cleanup/defer và identity quan sát được. Scope pure/graph resolve action nhưng không chạy user side effects hoặc start producer.

Các trường hợp cần tra lại source trước khi sửa: timer cùng ID không tự hủy timer cũ; statechart Stop không đồng nghĩa chạy exit actions; subscriber và emitted listener có quy tắc iteration khác nhau; pure helper có thể gọi context initializer nhiều lần. Các tính chất này không được đổi theo trực giác về API .NET.

**Hệ quả:** tái sử dụng actor ID không được chuyển delayed event sang actor mới. Inspection snapshot không đồng nghĩa snapshot đã publish. ExecutableAction giữ info/params tại thời điểm resolve; giữ action result cũng giữ captures của nó. Căn cứ: [Configuration.cs](Configuration.cs), [ActorTransitions.cs](ActorTransitions.cs), [ActorEmissions.cs](ActorEmissions.cs), [ActorScheduler.cs](ActorScheduler.cs), [ExecutableAction.cs](ExecutableAction.cs).

## XSTATE-D07 Lỗi là giá trị, Exception chỉ ở ranh giới .NET

**Quyết định:** Failure/error event/subscriber/reporter nhận `object?`; null cũng là lỗi hợp lệ khi Status=Error. Dùng `ActorErrors.ToException/GetValue` khi đi qua Task/IObserver/default reporter. Exception thật giữ identity. Reporter mặc định của child thuộc root system; override của actor chỉ áp dụng cục bộ.

**Lý do và hệ quả:** JS có thể throw/reject string, number, false, null hoặc object. Không được biến chúng thành message string hoặc exception mới trong contract nội bộ. JSON của plain Exception là `{}`; raw object phải giữ payload. Initial assign đang vi phạm contract này: XSTATE-R002 trong [CONTINUING.md](CONTINUING.md). Căn cứ: [ActorErrors.cs](ActorErrors.cs), [Actor.cs](Actor.cs), [ErrorValueTests.cs](../XState.Tests/ErrorValueTests.cs).

## XSTATE-D08 Missing, null, identity và context mutation

**Quyết định:** dùng presence flags khi khác biệt missing/null ảnh hưởng hành vi: HasOutput, HasContext, HasParameters, InputValue/OutputValue và CancellationReason. Registry/action properties giữ reference theo upstream. `Provide`/`Extend` merge những nhóm mà API đó sở hữu. `Assign<TContext>` trả toàn bộ context native; dictionary Assign/AssignPartial shallow-merge.

**Lý do và hệ quả:** null không thể thay undefined một cách tổng quát. Deep clone sẽ làm sai object identity và chi phí runtime. Property assignment đọc context cũ, rồi merge sau khi resolve; object nested tiếp tục dùng chung reference. Mutation registry phải được serialize cùng actor, không ghi Dictionary từ nhiều thread. Đây mới là ánh xạ trong phạm vi đã test, không phải mọi JS object semantics.

Căn cứ: [MachineSnapshot.cs](MachineSnapshot.cs), [MachineSetup.cs](MachineSetup.cs), [PropertyAssignment.cs](PropertyAssignment.cs), [ObjectAction.cs](ObjectAction.cs), [ActorOptions.cs](ActorOptions.cs).

## XSTATE-D09 Snapshot persistence khác definition JSON

**Quyết định:** persist actor bằng GetPersistedSnapshot/SnapshotJson; restore children bằng nguồn của machine đích trước khi revive actor references trong context. Inline invoke có canonical source theo state-node/index; spawned inline actor bị từ chối mặc định, chỉ có opt-in unsafe trong bộ nhớ. Không chạy lại entry/context initializer khi restore.

Definition JSON phục vụ cấu trúc/metadata. Serialize không chạy callbacks; JSON không khôi phục function đã bỏ. Function-valued object property bị bỏ, array slot thành null. Giữ mutation semantics khi tái dùng cùng persisted object: có thể giữ reference từ lần restore trước.

**Trade-off:** copy-on-change và reflection phục vụ context C# nhưng không đảm bảo mọi container, cycle hoặc arbitrary runtime object. Round-trip definition có initial.target dạng array ID vẫn có thể lỗi khi actor khởi tạo, giống test upstream; không tự sửa ID để che khác biệt. Context factory vẫn do caller cung cấp.

Căn cứ: [MachinePersistence.cs](MachinePersistence.cs), [PersistedContext.cs](PersistedContext.cs), [SnapshotJson.cs](SnapshotJson.cs), [MachineDefinitionJson.Read.cs](MachineDefinitionJson.Read.cs), [FunctionJson.cs](FunctionJson.cs).

## XSTATE-D10 Graph và TestModel giữ semantics của thuật toán gốc

**Quyết định:** structural graph dùng compiled node identity; traversal dùng scope inert với empty actor thật. Serializer định danh vertex. Simple paths dùng stack tường minh để tránh CLR stack overflow nhưng giữ thứ tự DFS. Limit giới hạn adjacency traversal, không giới hạn số simple paths.

**Hệ quả:** graph có context tăng vô hạn cần giới hạn từ caller; full path materialization có thể tốn nhiều bộ nhớ. `JoinPaths` yêu cầu snapshot identity tại điểm nối. TestModel chạy event callback trước state callback. `PathTestException` giữ lỗi gốc qua InnerException vì Exception.Message của .NET không mutable như JS; exception identity này là khác biệt có chủ ý, không tuyên bố identical API.

Căn cứ: [Graph/Adjacency.cs](Graph/Adjacency.cs), [Graph/SimplePaths.cs](Graph/SimplePaths.cs), [Graph/TestModel.cs](Graph/TestModel.cs), [Graph/GraphActorScope.cs](Graph/GraphActorScope.cs).

## XSTATE-D11 Kiểm kê coverage không được bỏ assertion

**Quyết định:** tách số khai báo AST, runtime cases đã expand, compiler assertion groups, supplemental .NET, differential JS và examples. Giữ từng ID upstream, kể cả occurrence suffix. Translator chỉ nhận nguyên testcase/config mà nó biểu đạt đầy đủ; unsupported phải pending hoặc port tay, không bỏ assertion khó.

Compiler fixtures phải chứng minh positive compile và negative diagnostic đúng mã/dòng; transpile bỏ TS type không được tính type parity. Bằng chứng compiler gắn DLL, fixtures, harness và compiler fingerprint.

**Lý do và hệ quả:** tăng pass bằng cách thu hẹp inventory làm mất mục tiêu port. Không cộng supplemental probes/fuzz vào số upstream đã port. Xem [hướng dẫn harness](../XState.Tests/README.md), [compiler-contracts.mjs](../XState.Tests/tools/compiler-contracts.mjs), [compiler-cases/manifest.json](../XState.Tests/compiler-cases/manifest.json).

## XSTATE-D12 Gate cần thực thi và bằng chứng cùng lượt

**Quyết định của runner hiện tại:** Test-Parity luôn chạy upstream baseline, native/resource, differential JS, example behavior/CLI/upstream và compiler; `-RunUpstream` cũ đã bỏ. `run-evidence.mjs` tạo runId, capture SHA256 artifacts qua sáu stage source/upstream/native/differential/examples/compiler, rồi verify trước parity-report. Bắt đầu run mới xóa report cũ; run dở/fail không được coi là verified.

**Lý do:** report rời rạc hoặc differential không chạy không chứng minh kết quả của cùng lượt. Manifest hiện ràng buộc pin và hash artifacts; nó không thay việc review source hoặc chứng minh snapshot toàn working tree. Không chạy hai gate hoặc runner ghi chung `tmp/xstate-parity/` đồng thời.

Strict gate phải fail nếu coverage/examples chưa complete. Exit 1 do thiếu parity khác test crash: kiểm report, status và log để phân biệt. Không nới gate hay sửa manifest để làm xanh. Căn cứ: [Test-Parity.ps1](../XState.Tests/tools/Test-Parity.ps1), [run-evidence.mjs](../XState.Tests/tools/run-evidence.mjs), [parity-report.mjs](../XState.Tests/tools/parity-report.mjs). Thời điểm kiểm chứng runner mới được ghi riêng trong PORT-STATUS; có implementation chưa đủ để ghi pass.

## XSTATE-D13 Examples phải chạy entrypoint và dịch vụ thật

**Quyết định:** machine/flow native dùng chung giữa CLI và behavior checks; upstream runner import entrypoint gốc, alias xstate về source pin. Kiểm snapshots/context/output presence/completion/logs và case input khác. Native/upstream examples cùng runId và source digest; CLI phải thực sự thực thi và exit đúng.

Native behavior checks nằm trong XState.Examples.Tests, reference project examples; XState.Tests chỉ reference runtime. Gate lint/build và gọi hai runner riêng. Ranh giới này giữ core harness độc lập với sample application, còn pipeline chung vẫn chịu trách nhiệm kiểm toàn bộ phạm vi. Lượt 930c7d4e-b49e-4a45-9fee-c21cf05c67b4 đã chạy gate với cấu trúc này; mốc verified trước khi tách project được giữ riêng trong nhật ký.

Console dialogue phải gửi từng câu trả lời sau prompt tương ứng; bơm toàn bộ stdin trước làm Node readline mất câu trả lời. Process harness có timeout/output cap, đóng stdin và thu exit/stdout/stderr. Sample timer giữ delay thật; dịch vụ MongoDB/HTTP của example còn thiếu phải được chạy và verify trước khi tính pass.

Căn cứ: [XState.Examples/README.md](../XState.Examples/README.md), [WorkflowExecution.cs](../XState.Examples/WorkflowExecution.cs), [run-example-process.mjs](../XState.Tests/tools/run-example-process.mjs), [Run-ExampleCli.mjs](../XState.Tests/tools/Run-ExampleCli.mjs).

## XSTATE-D14 Tối ưu sau correctness và giữ bằng chứng đo

**Quyết định:** đo Release trước/sau cùng workload, warmup, sample count, tiered compilation và host load. Theo dõi allocation/GC, throughput, p95/p99 và ownership/thu hồi khi stop. Metadata/view/cache được cấp phát lazy khi có lợi theo phép đo; không cache state mutable làm sai observable behavior.

Các quyết định đã có trong nhật ký gồm lazy emitted listeners, lazy StateNode views, cached pre-initial value, optional metadata payload, canonical invoke-source cache và bỏ closure capture dư trong compile delayed transitions. Mỗi con số chỉ áp dụng workload/binary của đợt đó.

**Hệ quả:** máy dùng chung và lượt đo khác giờ chưa đủ kết luận latency tốt hơn hoặc không regression. Benchmark C# không chứng minh performance parity với JS hay game. Không đổi scheduling, thứ tự callback, validation hoặc cleanup để cải thiện benchmark. Xem toàn bộ số trước/sau và giới hạn trong [PORT-STATUS.md](PORT-STATUS.md).

## XSTATE-D15 Repository độc lập và CI cho test/examples

**Quyết định ngày 2026-10-06:** source XState, console test runners, examples, MIT license và tài liệu được tách thành repo public xstate-dotnet. Bốn project nằm dưới src/; tools/Lint-CSharp.ps1, Directory.Build.props, .editorconfig và global.json thuộc repo này. Không dựa vào cấu hình/build script của workspace chứa nó. Node/source pin và artifacts tiếp tục ở data/ và tmp/, đều gitignored.

**Lý do và hệ quả:** commit/release của thư viện có vòng đời riêng. Workspace sử dụng thư viện có thể pin một commit bằng submodule; thay đổi thư viện và cập nhật con trỏ submodule là hai commit riêng. Không publish cache, lịch sử Git hoặc source của host application. Migration đổi đường dẫn harness và inventory reader của example; giữ nguyên semantics runtime và assertion inventory.

**CI:** GitHub Actions trên Windows chạy setup pin, toàn bộ pipeline đã đăng ký gồm lint/native/upstream/differential/examples/CLI/compiler, rồi pack thư viện thành artifact. Workflow chạy cho push main, pull request và manual dispatch, actions pin SHA và chỉ cần contents:read. Failed test làm job fail; pending coverage được hiển thị trong summary và report. Manual require_complete bật strict gate, không nới định nghĩa complete để làm CI xanh. Gói NuGet là artifact kiểm tra; chưa tự publish NuGet.org hoặc deploy host. Evidence có retention 14 ngày; mốc lịch sử quan trọng phải ghi trong PORT-STATUS.

Căn cứ: [workflow](../../.github/workflows/ci.yml), [README repo](../../README.md), [lint](../../tools/Lint-CSharp.ps1), [contributor instructions](../../AGENTS.md). Harness hiện verify trên Windows x64; không suy ra Linux/macOS đã được kiểm chứng từ target net8.0.

Fixture có provenance hash theo raw bytes phải giữ nguyên bytes khi checkout. Riêng upstream-directed-graph.json dùng Git attribute -text: đổi CRLF thành LF làm hash sai dù JSON tương đương. Giữ cả hash check và so cấu trúc JS/C#; không đổi expected hash hoặc bỏ assertion để làm CI pass.
