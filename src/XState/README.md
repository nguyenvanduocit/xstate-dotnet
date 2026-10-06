# XState cho .NET

Repo độc lập: đọc [README tại root](../../README.md) và quyết định XSTATE-D15 trước khi chạy lệnh hoặc tích hợp. Source paths hiện dùng src/; bằng chứng migration mới nhất ở đầu PORT-STATUS.

Đang port package lõi XState 5.33.2 sang C#/.NET 8. Chưa hoàn thành, chưa tích hợp vào OhMyBot.

## Đọc gì để làm tiếp

- [CONTINUING.md](CONTINUING.md): bắt đầu phiên mới, hai lỗi review còn mở, backlog và điều kiện đóng một batch.
- [DECISIONS.md](DECISIONS.md): 14 quyết định về scope, execution domain, ownership, error/persistence, test gate và tối ưu.
- [PORT-STATUS.md](PORT-STATUS.md): mốc đã kiểm chứng hiện hành và toàn bộ nhật ký/số đo lịch sử.
- [Harness kiểm thử](../XState.Tests/README.md): setup, lint, native/differential/compiler/examples, artifacts và cách đọc exit code.
- [Examples native](../XState.Examples/README.md): CLI, dialogue và cách mở rộng integration tests.

Mốc verified 2026-10-06: 1.097/1.755 runtime cases upstream đã port, 7/453 compiler groups, 8/31 examples; 363 supplemental .NET và 650 differential JS pass. Chưa full parity. Hai sai lệch runtime đã tái hiện nhưng chưa thành regression của gate: WaitFor timeout ngoài actor turn và mất raw failure từ initial assign. Xem CONTINUING trước khi dùng các kết quả pass này để quyết định tích hợp.

## Phạm vi và tiêu chí hoàn thành

- Pin nguồn và test bằng commit trong `upstream.json`; giữ MIT license của upstream.
- Port toàn bộ core: statechart phân cấp/parallel/history, actor system, actions, guards, scheduler, persistence, inspection, utilities và graph.
- Mỗi test upstream phải có bản C# tương đương và bằng chứng chạy pass. Test parameterized phải được mở rộng thành từng trường hợp. Test skip/todo upstream giữ trạng thái công khai, không được tính pass.
- Các assertion chỉ dành cho type của TypeScript phải có ánh xạ sang kiểm tra compile C#; không tính chúng là runtime parity sau khi transpile bỏ type.
- Examples được coi là bài kiểm thử tích hợp bắt buộc. Theo yêu cầu user, loại 18 example có UI tương tác; 31 example còn lại phải chạy native và pass các kiểm tra hành vi đối chiếu upstream. Build-only, skip, thiếu dịch vụ, thiếu assertion hoặc thiếu một phía kết quả đều không tính pass. Danh sách nguồn/hash nằm ở `../XState.Tests/examples-inventory.json`; lý do và source chứng minh UI nằm trong `upstream.json`.
- Bộ test C# pass một phần không chứng minh 100% parity. Bảng kiểm kê vẫn giữ mọi test chưa port.
- Đo Release: allocation, throughput, p95/p99, vòng đời actor/timer/subscription và chạy dài. Không suy ra hiệu năng C# từ JavaScript.

## Kiến trúc

Thư viện độc lập với DPB và UI. Cấu hình graph thuộc machine, snapshot thuộc actor. Xử lý sự kiện tuần tự theo macrostep/microstep của upstream; callback, timer và actor con có owner rõ ràng. Không nhúng JavaScript vào runtime C# để thay thế phần chưa port.

Đối chiếu source upstream trước mỗi nhóm hành vi. Chỉ tối ưu sau khi test parity của hành vi đó pass; ghi rõ thay đổi biểu diễn dữ liệu và số đo. Không tự thay đổi thứ tự sự kiện, guard, action hay hủy tác vụ nhằm làm benchmark đẹp hơn.

## Theo dõi

`../XState.Tests/upstream-inventory.json`: kiểm kê khai báo test bằng AST, gồm hash file và các test động. Báo cáo runtime Vitest trong `tmp/xstate-parity/` bổ sung số ca thực tế sau khi expand vòng lặp/each. `PORT-STATUS.md` ghi trạng thái hiện tại và việc tiếp theo.


## Clock và lượt thực thi

RealClock là clock mặc định. Timer của RealClock và actor operations chạy tuần tự qua reentrant gate; các clock trên cùng SynchronizationContext dùng chung hàng đợi deadline. Nếu có context, callback được Post về context đó; nếu không có, callback dùng ThreadPool. Runtime không tự tạo thread riêng.

Dùng ActorRuntime.Run với delegate đồng bộ khi một chuỗi lệnh phải tương ứng với cùng một lượt JS, ví dụ Start → Send → đọc snapshot trước khi timer zero-delay chạy. Không await hoặc chờ đồng bộ một lượt khác trong delegate này. Các lệnh ngoài Run có thể bị timer xen giữa. Đây là ranh giới concurrency của bản C#, chưa phải tuyên bố đầy đủ async/microtask parity. WaitForAsync hiện dùng timer riêng ngoài gate; lỗi XSTATE-R001 trong [CONTINUING.md](CONTINUING.md) ghi cách tái hiện và tiêu chí sửa.

Host cần thread cố định có thể cung cấp SynchronizationContext hoặc ActorOptions.Clock. SimulatedClock vẫn gọi callback trên thread đang Increment/Set; actor con mặc định dùng scheduler/clock của root. Scheduler restore và các trường hợp JSON persistence còn thiếu đang được port.


## Promise actor

PromiseLogic<TOutput> nhận creator trả Task<TOutput>, với input/self/system/emit và CancellationToken riêng cho mỗi invocation. ActorMicrotasks xếp reaction sau lượt đồng bộ; ActorRuntime.YieldAsync cung cấp checkpoint cho code C#. Stop hủy token, bỏ delivery đang chờ và tách reference runtime tới actor; Task hoàn thành trễ không publish. CTS được giữ đến khi Task kết thúc để token còn dùng được trong quá trình hủy.

Đã kiểm chứng resolve, lifecycle, spawn/invoke, input/emit, snapshot restore trong bộ nhớ và một số kiểm tra thu hồi capture. Callable thenables đã có API riêng và JSON persistence có phạm vi kiểm chứng bên dưới; còn thiếu toàn bộ Promise.resolve contract và đầy đủ ordering của Task continuation so với Promise JS. Xem PORT-STATUS.md cho số test, phép đo và phần còn thiếu.


## Observable actor

ObservableLogic<TContext> nhận IObservable<TContext>, dùng giá trị next làm context snapshot. EventObservableLogic nhận IObservable<MachineEvent> và gửi event tới parent; context của nó không thay đổi theo next. Snapshot dùng HasContext để phân biệt chưa có giá trị với giá trị null/default.

Stop dispose subscription; complete/error theo ownership của upstream, producer tự kết thúc tài nguyên của nó. Snapshot persistence trong bộ nhớ bỏ handle subscription và subscribe lại khi restore active. Raw error values qua IObserver dùng ActorErrors carrier; JSON primitive/object/null và plain Exception đã có kiểm chứng. Toàn bộ compile-time API còn thiếu. Xem PORT-STATUS.md cho phạm vi kiểm chứng và phép đo.


## Machine persistence

GetPersistedSnapshot lưu cây actor, state/history, output/error và context trong bộ nhớ. Restore dùng source của machine đích, khôi phục child trước khi gắn reference trong context; entry actions và context initializer không chạy lại. Spawned inline actor mặc định bị từ chối; PersistenceOptions.UnsafeAllowInlineActors cho phép lưu nguồn inline trong bộ nhớ.

PersistedContextValue<TContext> giữ kiểu C# và ID actor trong nhánh context thay đổi. JSON round-trip có phạm vi kiểm chứng bên dưới; context cyclic, context initialization failure và arbitrary runtime object chưa được hỗ trợ đầy đủ. Restore còn giữ hành vi mutation của upstream: tái dùng cùng persisted object có thể giữ reference của lần restore trước. Phạm vi test, GC và benchmark nằm trong PORT-STATUS.md.


## JSON snapshots

Dùng SnapshotJson.Serialize(actor.GetPersistedSnapshot()) để lấy JSON, rồi truyền SnapshotJson.Parse(json) vào ActorOptions.Snapshot. Đã kiểm trao đổi một cây actor có nested references với XState JS pin ở cả hai chiều. Không nhúng JS vào thư viện runtime C#.

Custom error properties, function/inline logic, đầy đủ null/undefined, computed context properties và một số container/type đặc biệt còn thiếu. Serializer không phải chứng nhận full parity hoặc hiệu năng game; xem PORT-STATUS.md cho test, fixture và số đo.


## Inspection

ActorOptions.Inspect hoặc actor.System.Inspect nhận actor/event/snapshot, microstep và action events. Microstep có snapshot trung gian, event gốc và Transitions chứa source/targets, descriptor, guard/actions và reenter. Các bước always/raised trong khởi tạo có thể xuất hiện trước Start; không coi mỗi microstep là một snapshot đã publish tới subscriber.

Không có inspector thì runtime không tạo snapshot trung gian phục vụ inspection. Consumer tự quản lý thời hạn giữ event/snapshot và dispose subscription. Action inspection hiện có tên/params của custom action và các builtin đã port. StateNode, graph traversal và TestModel đã có API native được mô tả bên dưới; full public API/compiler parity vẫn chưa hoàn tất. Xem PORT-STATUS.md cho phạm vi và phép đo.


## Pure action results

ActorTransitions.Initial/Next và các helper machine trả Actions là danh sách ExecutableAction. Mỗi phần tử có Type, Info (context/event/self/system lúc resolve), Parameters và HasParameters. Pure calculation chỉ resolve, không thực thi side effect. Execute() chạy implementation đã resolve; action thiếu implementation là no-op và HasImplementation=false.

API cũ Effects chứa delegate đã được thay bằng Actions; caller test đã cập nhật. Khi giữ kết quả pure calculation, caller cũng giữ các actor/context/params được tham chiếu trong Info và Parameters. Xem PORT-STATUS.md cho phạm vi builtin, kiểm tra GC và allocation.


## Potential transitions

ActorTransitions.GetNextTransitions(snapshot) trả transition definitions từ các active leaf lên ancestor, theo thứ tự của upstream. Bao gồm mọi nhánh guarded, always và delayed transition; không evaluate guard hoặc chạy action. Kết quả là các khả năng trong cấu hình, không dự đoán nhánh Send sẽ chọn.

Mỗi lần gọi tạo danh sách riêng và dùng chung compiled definitions. API hoạt động cả với snapshot stopped/done. Phần graph và public StateNode API đầy đủ vẫn đang port; xem PORT-STATUS.md cho phạm vi test và chi phí truy vấn.


## Logging

MachineActions.Log<TContext>() ghi context/event; overload nhận value hoặc expression và label tùy chọn. ActorOptions.Logger nhận nguyên argument list qua ActorLogger. Actor con mặc định dùng logger của system; override logger của một actor chỉ áp dụng cho actor đó. Pure transition dùng logger inert, đồng thời trả metadata xstate.log để caller đọc.

Logger mặc định dùng console/ToString của .NET, chưa mô phỏng đầy đủ định dạng console.log của Node. Runtime không giữ lịch sử log; sink do caller cung cấp quyết định cách ghi và thời hạn giữ dữ liệu. Xem PORT-STATUS.md cho test, GC và phép đo.


## Pure microsteps

`ActorTransitions.GetMicrosteps(machine, snapshot, event)` trả từng `TransitionResult<TContext>` với snapshot và action riêng. `GetInitialMicrosteps(machine, input)` bao gồm bước entry ban đầu. Hai API resolve action nhưng không chạy side effect; giữ danh sách kết quả sẽ giữ cả context/actor mà action tham chiếu.

`StateMachine.Microstep(snapshot, event, scope)` trả riêng các snapshot và dùng executor/inspection của scope. Có thể tạo scope tính thuần bằng `ActorTransitions.CreateInertScope(machine)`. Khác API GetInitialSnapshot, lỗi tính initial microsteps được throw ra. Số đo allocation, test và các phần parity còn thiếu nằm trong PORT-STATUS.md.


## State nodes

`machine.Root` và `machine.States` cho phép duyệt compiled graph. `GetStateNodeById(id)` nhận ID có hoặc không có `#`, cùng path đã escape. Node có Parent, Machine, Path, Order, Config, Meta/Description; On/Transitions, Always/After/Initial cho phép đọc transition definitions mà không chạy actor. Node và transition giữ cùng identity với inspection/pure APIs.

`node.OwnEvents` trả event có transition hữu hiệu tại node; `node.Events`/`machine.Events` gồm descendants. `node.Next(snapshot, event)` đánh giá guards và trả definition được chọn, không chạy action. `TransitionConfig.Target = []` là target rỗng tường minh; bỏ Target hoặc gán null biểu diễn target không khai báo, có khác biệt trong Can/OwnEvents.

Các collection được cung cấp dưới dạng read-only. Definition, invoke metadata, definition JSON và graph algorithms đã có implementation trong phạm vi mô tả bên dưới; chưa đạt toàn bộ malformed-input, metadata và compile-time parity. Xem PORT-STATUS.md để chọn testcase còn pending.


## Structural graph

Dùng `XState.Graph.StateGraph.GetStateNodes(machine)` để lấy descendants theo preorder, hoặc `ToDirectedGraph(machine)` để dựng cây node và cạnh. Cũng có overload nhận một StateNode làm gốc subtree. Truy vấn dùng cấu trúc compiled, không tạo actor hay chạy guard/action.

`StateGraph.Serialize(graph)` xuất JSON dạng `{id, children, edges}`; mỗi edge chứa source/target ID và label.text. Các reference StateNode/Transition vẫn có trên object C# để caller tra cấu hình, nhưng không đi vào JSON. Cạnh từ initial/always không được đưa vào graph, theo upstream. `target: []` và target không khai báo cho kết quả khác nhau.

GetAdjacencyMap và GetShortestPaths đã có bản native trong phạm vi kiểm chứng bên dưới. GetSimplePaths/GetPathsFromEvents và phạm vi TestModel đã port được mô tả bên dưới. Test, fixture và số đo allocation của graph hiện có nằm trong PORT-STATUS.md.


## Empty actor và compiler tests

`Actors.CreateEmptyActor()` tạo actor chưa start, nhận mọi MachineEvent, không có context/output có giá trị. Start/Send/Stop và subscription dùng cùng lifecycle với các actor khác; mỗi event tạo snapshot mới. `SnapshotJson.Serialize(actor.GetPersistedSnapshot())` trả `{"status":"active"}` cho actor active. Xem PORT-STATUS.md cho các góc cạnh stop và số đo cấp phát.

`pwsh src/XState.Tests/tools/Test-Parity.ps1 -RequireComplete` chạy cả native runtime và Roslyn compiler fixtures. Manifest trong `XState.Tests/compiler-cases/` ghi declaration upstream, cách biểu đạt tương đương C#, positive/negative source và diagnostic mong đợi. Negative fixture chỉ pass khi đúng lỗi ở đúng dòng; kết quả compiler cũ bị từ chối khi DLL, fixture, compiler hoặc harness thay đổi. Hiện mới port 7/453 nhóm compiler assertions; gate đầy đủ vẫn fail cho các phần pending. Inventory bao gồm positive-only tests trong các file chuyên type và suite `type safety`, không chỉ các test có @ts-expect-error. Bộ control tests kiểm quy tắc phân loại này trước khi compile.


## ResolveState

`machine.ResolveState(value, context, status, output, failure, historyValue)` tạo snapshot từ state value một phần, bổ sung initial/parallel descendants và chuẩn hóa các nhánh. Nó không chạy context factory, entry actions, invoke hoặc always transitions. Context phải được cấp theo TContext; failure hiện nhận Exception.

Có thể cấp `Dictionary<string, StateNode<TContext>[]>` làm historyValue và đọc lại qua `snapshot.HistoryValue`. Map/arrays giữ identity đầu vào như upstream; caller quản lý mutation của history được chia sẻ. Khi value đã final, Status là Done dù status đầu vào khác; output/failure vẫn được giữ. Chi tiết test, numeric key ordering, các giới hạn parity còn lại và benchmark nằm trong PORT-STATUS.md.


## Snapshot ordering và metadata presence

Snapshot giữ insertion order của active nodes theo microstep upstream; external events chọn nhánh theo state value, eventless transitions theo active-leaf order. Thứ tự chạy entry/exit actions vẫn theo document order. Khi reentry kết thúc với cùng membership, snapshot giữ node order trước đó.

Tags giữ thứ tự xuất hiện đầu tiên và hỗ trợ IReadOnlySet membership. `StateNode.HasMeta` phân biệt metadata không khai báo với `Meta = null`; `snapshot.GetMeta()` giữ explicit null và xử lý numeric/duplicate IDs theo object semantics đã kiểm chứng. Metadata được chụp khi compile. Số đo hiệu năng, tests và những phần parity chưa hoàn tất nằm trong PORT-STATUS.md.


## Test fixtures và stateIn

Gate trích các cấu hình parallel literal từ AST source đã pin vào fixture, rồi đối chiếu snapshot C# với initializer JavaScript gốc. Callback chưa hỗ trợ bị từ chối nguyên fixture và port tay. Toàn bộ file runtime parallel.test.ts (28 test) và stateIn.test.ts (9 test) đã pass; đây là phạm vi từng file, chưa phải toàn bộ core XState.

Data tests hỗ trợ guard `stateIn` bằng string path, ID hoặc state value lồng nhau; guard kiểm tra state active qua MachineGuards.StateIn. Các test kiểm tra cả nhánh parallel, parent/sibling lồng nhau và eventless transitions. Xem PORT-STATUS.md và parity-report.json cho phần còn thiếu.


## Traversal và shortest paths

`StateGraph.GetAdjacencyMap(logic, options)` duyệt các snapshot và trả state/transition theo serializer. `GetShortestPaths` trả `StatePath<TSnapshot>` gồm State, Weight và Steps (step đầu là xstate.init). `JoinPaths` yêu cầu snapshot tại điểm nối có cùng identity.

`TraversalOptions<TSnapshot>` nhận `Events` dạng mảng MachineEvent hoặc `new TraversalEvents<TSnapshot>(snapshot => events)`, cùng FilterEvents, Limit, FromState, StopWhen/ToState, Input và serializer tùy chỉnh. Machine mặc định lấy event descriptors của các node active. Cần đặt Limit/StopWhen cho graph có context tăng không giới hạn; mặc định giữ Infinity của upstream. Mọi đường đi chứa đầy đủ steps nên bộ nhớ có thể tăng nhanh; số đo 16/64/256 state nằm trong PORT-STATUS.md.

Graph chạy bằng scope có Self là empty actor thật; assign được tính, user effects/defer và việc start actor con không chạy. `ActorScope.Self` và Self trong action/context args dùng IActor: Send, GetSnapshot, StartActor/StopActor, Subscribe, OnEvent. Khi cần context đặc thù, kiểm kiểu snapshot thay vì giả định mọi scope đều có machine actor.

Serializer mặc định hỗ trợ machine và các built-in snapshot hiện đã port; custom snapshot, toàn bộ JS object semantics và full compiler parity còn thiếu. Không dùng số test pass hiện tại để suy ra full XState parity.


## Simple paths và event sequences

`StateGraph.GetSimplePaths(logic, options)` liệt kê các đường đi không lặp vertex theo state serializer. Số đường có thể tăng nhanh theo graph; Limit giới hạn bước duyệt adjacency, không phải số kết quả simple paths.

`StateGraph.GetPathsFromEvents(logic, events, options)` trả path kết thúc sau chuỗi events, gồm xstate.init ở đầu. Nó dựng adjacency trước khi lấy path. Nếu không override Events, cả event sequence được dùng làm event cases, kể cả event trùng. Event không được machine xử lý thường vẫn có self-edge; cạnh bị filter loại sẽ gây lỗi lookup.

`FromState = null` tường minh khác với bỏ FromState khi khởi tạo defaults. Context initializer có thể được gọi nhiều lần theo thuật toán upstream; xem PORT-STATUS.md cho call order/input, coverage và allocation đo được.

Gate kiểm archive pin cùng mọi file core trước khi chạy test. `Verify-Upstream.ps1` và control tests ngăn việc vô tình dùng source/snapshot đã thay đổi làm oracle; report đầy đủ vẫn chưa đạt complete.


## Test models

`StateGraph.CreateTestModel(machine)` tạo model cho state machine, với coverage phân biệt transition. `new TestModel<TSnapshot>(logic, options)` dùng cho actor logic khác. Factory từ chối invoke/after và numeric inline delay theo validator upstream; việc tạo model không khởi động actor.

`GetShortestPaths`, `GetSimplePaths`, `GetPaths(generator)`, `GetShortestPathsFrom`, `GetSimplePathsFrom`, `GetPathsFromEvents` và `GetAdjacencyMap` trả các đường đi/cạnh. `TestPath.Description` mô tả state đích và event sequence. Mặc định bỏ path là prefix của path dài hơn; đặt `AllowDuplicatePaths = true` tại lời gọi khi cần giữ chúng.

`await path.TestAsync(new TestParameters<TSnapshot> { States = stateTests, Events = eventTests })` chạy event callback rồi state callbacks của mỗi step. Callback có kiểu Func<…, Task>; callback đồng bộ trả Task.CompletedTask. Model machine khớp state key hoặc #ID; wildcard là fallback. Options có thể cập nhật và theo dõi field được cấp để merge đúng; `GetPathsFromEvents` dùng options của chính lời gọi.

Khi callback lỗi, bản hiện tại dừng path và ném PathTestException có trace; InnerException giữ lỗi gốc. JS upstream sửa message và rethrow chính Error gốc, nên exception identity còn khác. Xem PORT-STATUS.md cho test đã port, phần pending và số đo allocation; chưa đạt full parity.


## Function trong context và event JSON

Delegate được giữ nguyên trong context/event ở bộ nhớ. Khi ghi snapshot JSON hoặc graph JSON, property/field/dictionary entry có giá trị Delegate bị bỏ, còn phần tử array được ghi null theo JSON.stringify. Serializer không chạy delegate. Snapshot persisted trong bộ nhớ vẫn giữ delegate; JSON đã bỏ function không thể khôi phục code của function đó.

Quy tắc này cũng áp dụng cho context đã thay actor references bằng ID marker. Context/event chỉ chứa function vẫn có enumerable keys, nên graph có thể ghi context `{}` hoặc event description `GO ({})`. Kiểm thử và số đo trước/sau nằm trong PORT-STATUS.md; đây chưa phải toàn bộ JavaScript object/serialization semantics.


## Selected values

`var selected = actor.Select(snapshot => snapshot.Context.Count);` tạo selection, chưa chạy selector. `selected.GetValue()` đọc giá trị hiện tại; `selected.Subscribe(value => ...)` trả IDisposable và chỉ phát khi giá trị thay đổi. Mỗi subscription có previous value riêng. IActorRef/IActor expose cùng chức năng bằng SelectValue; tên interface GetValue/SelectValue tránh CA1716.

Comparator mặc định xử lý NaN, signed zero và object identity theo Object.is trong phạm vi đã kiểm chứng. Có thể truyền comparator riêng; khi comparator coi hai giá trị bằng nhau, previous của subscription vẫn là giá trị đã thông báo gần nhất. GetValue luôn đọc snapshot mới nhất.

Selection observers chỉ nhận next; complete/error không được chuyển tiếp, đúng XState pin. Selector lỗi lúc GetValue/Subscribe được ném trực tiếp; lỗi lúc actor publication được actor reporter nhận. Dispose token hoặc actor stop tháo callback và giải phóng capture. Chi phí steady-state và giới hạn ánh xạ native value types được ghi trong PORT-STATUS.md.


## MapState

`StateMapping.MapState(snapshot, mapper)` trả danh sách `StateMapResult<TContext, TResult>` gồm StateNode và Result. Tạo `StateMapper<TContext, TResult>` với Map nhận snapshot và States ánh xạ child key sang mapper con. Chỉ active atomic/final nodes và ancestor được xét, đi từ leaf lên root, ancestor chung chạy một lần. Node không có Map không tạo result.

Mapper được đọc lại theo path trước mỗi callback; callback thay mapper hoặc gửi event vẫn giữ snapshot đầu vào cho lần MapState hiện tại. Kết quả dùng compiled node identity, giữ cả result null; callback lỗi dừng truy vấn và ném cùng exception. State keys hiện dùng dictionary string, chưa có compile-time validation theo machine schema. Xem PORT-STATUS.md cho test/compiler coverage và chi phí truy vấn.


## Final-state output

`StateConfig<TContext>.Output` nhận `MachineOutputArgs<TContext>` với Context, Event và Self. Ví dụ `Output = args => args.Context` hoặc `Output = args => args.Self`. Final child mapper nhận triggering event; root mapper nhận done-state event của completion node. Entry assign chạy trước mapper, exit actions chạy sau khi root output đã được tính.

`Output = _ => null` biểu đạt output null; không cấu hình Output biểu đạt output vắng. Dùng `snapshot.HasOutput` để phân biệt, vì `snapshot.Output` trả null cho cả hai trường hợp. JSON live/persisted và restore giữ khác biệt này. Khi resolve snapshot trực tiếp với null output, truyền `hasOutput: true` vào ResolveState. Đây chưa phải biểu diễn đầy đủ mọi giá trị undefined của JavaScript.


## Callable thenables

Dùng `PromiseActors.FromPromiseLike<TOutput>(creator)` khi producer trả `IPromiseLike<TOutput>`. Interface cung cấp getter `ThenHandler`; delegate nhận `PromiseResolver<TOutput>`. Gọi Resolve(value) để fulfill, Adopt(otherThenable) để nhận kết quả của thenable khác, hoặc Reject(failure) với raw value, kể cả null. Chỉ lần gọi đầu tiên có hiệu lực; throw sau settlement bị bỏ qua như Promise.resolve.

Getter chạy ngay khi giá trị được nhận; ThenHandler chạy trong microtask và actor reaction chạy sau đó. Stop hủy signal và ngăn late publication; queued then vẫn được thực thi. Resolver do producer giữ sau Stop không giữ actor qua bridge nội bộ. Producer cần kết thúc và settle để tài nguyên token còn đang dùng được dispose.

Constructor `new PromiseLogic<TOutput>(creator)` tiếp tục nhận Task. Thenable API hiện hỗ trợ callable then và raw rejection values. Non-callable then và toàn bộ Promise.resolve/type contracts còn được theo dõi trong PORT-STATUS.md.


## Error values và .NET boundaries

Snapshot.Failure, ActorErrorData.Failure, action Subscribe(onError:) và IUnhandledErrorReporter.Report nhận object?, giữ null như giá trị lỗi hợp lệ khi Status là Error. Actor con mặc định dùng reporter của root system; ActorOptions.ErrorReporter override riêng cho actor đó.

Task và IObserver yêu cầu Exception. Dùng ActorErrors.ToException(value) tại ranh giới đó, ActorErrors.GetValue(exception) để đọc lại. Exception thật giữ identity; contract của native actor callbacks là nhận raw value trực tiếp. Nhánh initial assign đã phát hiện còn giữ carrier exception, được theo dõi bằng XSTATE-R002 trong [CONTINUING.md](CONTINUING.md). Ví dụ producer: Task.FromException<int>(ActorErrors.ToException("failed")). Thenable resolver dùng Reject("failed") trực tiếp.

WaitForOptions.CancellationReason phân biệt không cấu hình với explicit null: có reason thì Task fault với reason, không cấu hình thì token làm Task canceled. JSON snapshot ghi primitive/object/null và plain Exception thành {}; custom Error properties và đầy đủ undefined semantics vẫn đang port.


## Forwarding

MachineActions.ForwardTo<TContext>("child", options) gửi lại nguyên MachineEvent đang xử lý đến child ID, dùng pipeline SendTo và giữ delay/cancel/action metadata. Target null hoặc chuỗi rỗng chuyển actor sang lỗi tại lúc action được resolve. Overload expression trả IActor hỗ trợ chọn actor theo context. Direct actor-ref argument, expression trả string và options cho expression vẫn đang port; xem PORT-STATUS.md.


## Assign một phần context

Với context `IReadOnlyDictionary<string, object?>`, `MachineActions.AssignPartial` nhận hàm trả dictionary chứa các thuộc tính cần thay đổi; `MachineActions.Assign` nhận dictionary `ContextPropertyAssignment`. `ContextPropertyAssignment.Value(value)` gán hằng; `Expression(args => ...)` đọc context/event/params và có thể spawn. Các property đọc chung context trước assignment; sau khi resolve hết, runtime shallow-merge vào dictionary mới, giữ các key không được cập nhật. Null được lưu thành key có giá trị null, không xóa key.

Overload explicit generic `Assign<TContext>` nhận toàn bộ giá trị context C#; dùng `with` khi TContext là record. Khi enqueue dạng dictionary partial, dùng `Enqueue.Add(MachineActions.AssignPartial(...))` hoặc `Enqueue.Add(MachineActions.Assign(...))`. Context dictionary mới là read-only; object nested vẫn giữ reference. Không có deep clone. Xem PORT-STATUS.md cho giới hạn JS keys/undefined và phép đo chi phí.


## Action events và cảnh báo

`Raise` và `SendTo` nhận event `MachineEvent` tĩnh hoặc expression; SendTo có target ID, target expression hoặc `IActor` trực tiếp. Overload nhận string event báo lỗi khi action resolve, giống validation của upstream. Delayed send giữ actor instance đã resolve: tái sử dụng ID không chuyển event đang chờ sang actor mới.

`SendParent` cũng nhận `MachineEvent` tĩnh hoặc expression, dùng chung tùy chọn delay/id với `SendTo`. Event tĩnh giữ nguyên reference khi gửi; cancel theo ID và stop của actor gửi dọn timer theo vòng đời actor.

`ActorOptions.Warning` trên root actor nhận development warning cho cả system; mặc định ghi Console.Error. Cảnh báo hiện bao gồm gửi tới actor đã dừng và gọi assign/raise/sendTo/emit bên trong custom action. Scope được khôi phục khi reentrant hoặc throw, không giữ actor trong thread-local sau khi action kết thúc. Callback warning là đồng bộ; host nên giữ ngắn và tự quản lý lưu trữ log. Xem PORT-STATUS.md cho giới hạn event payload/diagnostic parity và phép đo allocation/latency.


## Lỗi initial state và snapshot

Target initial không tồn tại được kiểm khi state được duyệt. Constructor machine vẫn tạo được cấu hình đó; khi actor khởi tạo hoặc chuyển vào nhánh sai, lỗi được đưa vào snapshot theo vòng đời upstream. Compound state thiếu khai báo initial vẫn bị từ chối lúc tạo machine.

Nếu chưa tạo được skeleton state value, error snapshot có `HasStateValue=false`, `HasContext=false`; kiểm các cờ này trước khi đọc Value/Context. JSON của snapshot đó chỉ chứa status/error. Nếu đã có snapshot trước lỗi, value/context của snapshot đó được giữ. Initial value hợp lệ được cache dùng chung trong machine, không gọi lại context factory khi chỉ đọc snapshot.


## Event nhận từ giá trị động

`Actor<TSnapshot>.Send(MachineEvent)` là đường gửi typed hiện có. Overload `Send(object)` kiểm giá trị động: MachineEvent được gửi nguyên reference; chuỗi bị từ chối trước inspection/relay với thông báo của upstream. Những CLR object khác cần được chuyển thành MachineEvent; overload này không phải parser cho mọi JS object. Kiểm tra compiler contract của toàn bộ event API vẫn chưa hoàn tất.


## Bảng testcase upstream

`Test-Parity.ps1` tự trích các bảng transition từ nguồn pin trước khi build. Extractor chỉ nhận toàn bộ cấu hình/expected/loop mà nó biểu đạt được, có controls chống bỏ sót assertions. Các ca được mở rộng giữ ID riêng trong parity-report. JavaScript đối chiếu chạy machine và expected từ biểu thức upstream gốc; C# dùng fixture JSON và kernel native. Bảng bị từ chối tiếp tục được theo dõi ngoài phần đã chuyển.



## Machine setup và registries

`MachineSetup<TContext>` nhận schemas và các dictionary actors/actions/guards/delays. `CreateMachine(config, contextFactory)` copy root config, giữ nested state references và dùng schemas của setup. `Extend` merge actions/guards/delays thành registry mới, giữ actors và schemas. `CreateStateConfig`/`CreateAction` giữ nguyên object/delegate. Các action helpers trên setup dùng chung implementation với MachineActions.

`StateConfig.Version` và `Schemas` được expose qua machine và giữ khi Provide. Schema là opaque object, không có validation hay deep clone. Metadata payload chỉ được cấp phát khi có giá trị.

Machine giữ reference của registry do caller cung cấp, giống upstream. Thay dictionary entry sau khi tạo machine ảnh hưởng các lần resolve tiếp theo; actor con đã tạo không bị thay logic giữa chừng. Serialize mutation cùng actor bằng `ActorRuntime.Run`; không ghi Dictionary đồng thời từ thread khác. Provide và Extend tạo registry merge riêng cho những nhóm được chúng merge.

Setup hiện ràng buộc context C#; event unions, registry keys và parameter typing vẫn cần compiler parity. Context factory là argument riêng. Generic Assign trả toàn bộ context; dùng MachineActions.AssignPartial cho dictionary partial update. Xem PORT-STATUS.md cho giới hạn schema/definition và phép đo memory.


## Definition và action metadata

Machine.Definition và StateNode.Definition trả tree mới chứa cấu trúc statechart, initial/ordinary transitions, actions và invokes. Source/target vẫn là compiled nodes; On giữ transitions gốc. ActionDefinition gồm Type, HasParameters và Parameters; expression được giữ nguyên delegate, không chạy khi đọc definition. Entry/exit gồm cả raise/cancel do after sinh ra.

Type của builtin có dạng xstate.assign, xstate.sendTo… Custom action dùng tên truyền vào hoặc tên CLR suy ra; anonymous có chuỗi rỗng. Runtime inspection vẫn dùng nhãn (anonymous) khi không có tên. Invoke Src là tên canonical, gồm cả nguồn inline được machine đăng ký.

Dùng MachineDefinitionJson.Serialize(machine), Serialize(node) hoặc Serialize(definition) để xuất JSON. Source/target trở thành #ID, initial/entry/exit chuyển actions thành object metadata; transition thường giữ raw string/object và ghi null cho action dạng hàm. Invoke bỏ onDone/onError khỏi invoke object vì các transition này đã nằm trong On; onSnapshot vẫn giữ trong invoke theo upstream. Callback guard/input/output và dynamic params không được chạy khi serialize. Không dùng JsonSerializer mặc định cho definition có vòng tham chiếu.

StateConfig.OutputValue khai báo output hằng, gồm explicit null; Output tiếp tục nhận expression. Chỉ chọn một trong hai. Root/final state giữ output, state thường bỏ qua. Literal giữ reference khi phát done event và khi hoàn tất machine; setup copy cũng giữ giá trị. Definition expose HasOutputValue/OutputValue, JSON chứa output literal nhưng bỏ output expression.

MachineActions.FromProperties<TContext>(properties) nhận action object có type, params và extra properties. Giữ dictionary reference: mutation type/params ảnh hưởng lần resolve tiếp theo và metadata đang giữ. exec là metadata, không thay action implementation trong registry. Serialize mutation cùng actor qua ActorRuntime.Run nếu dùng từ nhiều thread.

Cả ba ca json.test.ts đã port. Schema tests dùng JsonSchema.Net trong project test và nguyên schema upstream, đối chiếu Ajv. Runtime không tự validate schema và không phụ thuộc package validator.

MachineDefinitionJson.Parse<TContext>(json) trả StateConfig để truyền vào StateMachine cùng context factory của caller; definition JSON không lưu context. Parser đọc cấu trúc state/transition/actions/guards/invokes, output và metadata được biểu đạt. SerializeTransition<TContext> xuất một compiled/definition transition. Definition action arrays cho phép null; null action bị gọi sẽ đưa actor sang error, giống upstream.

InvokeConfig.InputValue khai báo input hằng, gồm explicit null; Input nhận expression. Chỉ dùng một trong hai. Giá trị được giữ reference khi tạo child, có mặt trong definition/JSON qua HasInputValue/InputValue.

Round-trip definition giữ giới hạn của upstream: callback đã mất trong JSON không được phục hồi; initial.target dạng mảng ID tuyệt đối vẫn được dùng như key cục bộ khi đọc lại, nên ví dụ round-trip invoke upstream kiểm tra được transitions nhưng actor khởi tạo gặp lỗi initial. Dùng snapshot persistence cho trạng thái actor. Metadata tùy ý của transition/guard, một số missing/undefined/null distinctions và malformed input semantics chưa đầy đủ; 3 testcase JSON pass không chứng minh toàn bộ JSON/public API hoàn tất. Xem PORT-STATUS.md.


## Executable examples

Các example không UI có project console riêng tại `../XState.Examples/`. Hiện 7/31 example bắt buộc chạy được và pass đối chiếu hành vi; xem README của project để chạy CLI. `Test-Parity.ps1` chạy cả native behavior checks, từng CLI và entrypoint upstream thật trước khi tính example pass. 18 example UI được loại theo yêu cầu user, không cộng vào số pass.


## Chạy và so sánh JS/C# tự động

Thiết lập bằng `pwsh -NoProfile -File src/XState.Tests/tools/Setup-Upstream.ps1`, sau đó chạy `pwsh -NoProfile -File src/XState.Tests/tools/Test-Parity.ps1 -RequireComplete`. Lệnh luôn chạy lại suite JS upstream, native tests, JS/C# differential probes, examples và compiler contracts. Không còn tùy chọn RunUpstream; báo cáo cũ không thay thế một lượt chạy mới.

[README của harness](../XState.Tests/README.md) mô tả từng bước và artifact. `run-evidence.json` lưu mã lượt chạy cùng hash các kết quả; `parity-report.json` chỉ được tạo sau khi tất cả bước thực thi đã pass. `complete` vẫn false khi còn runtime/compiler/example pending. Không dùng số differential probes để suy ra toàn bộ test đã port.


## History và internal transitions

Đã port toàn bộ runtime assertions của history.test.ts (35/35) và internalTransitions.test.ts (12/12), gồm initial/entry effects, invocation restart và revived StateNode history. Bộ đối chiếu JS/C# kiểm thêm state/history IDs và action/counter traces. Đây là coverage của hai file; toàn thư viện vẫn còn các test/compiler/example pending.
