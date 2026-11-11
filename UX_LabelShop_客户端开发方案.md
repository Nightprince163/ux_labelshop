# UX_LabelShop 桌面客户端开发方案（纯 WPF 重写版）

> 版本：v2.0（重写方案，替代 v1.x 过渡方案）
> 日期：2026-11-04
> 状态：待评审，**暂未开始编码**
> 策略：**抛开现有代码全部重写**；仅在 `.lsd` 文件识别转换、打印格式（幅面/DPI/条码规格）实现时参考 `label-system-kit/` 与 `agent/` 中的既有逻辑

---

## 1. 目标与范围

做一个 **.NET 8 纯 WPF 单文件桌面客户端**（Win11 运行），直连服务器 MySQL，替代 LabelShop：

| # | 功能 | 说明 |
|---|---|---|
| 1 | 登录 + 分级权限 | 用户（操作员）/ 管理员 两级 |
| 2 | 扫码打印 | 选模板 → 扫码解析 → 待打印清单（含**空白单元格批量填充**）→ 预览 |
| 3 | 打印预览 | 预览、设置打印参数、选择打印机 |
| 4 | 打印记录 | 打印历史查看与追溯 |
| 5 | 模板设置 | 模板编辑、模板管理、新增模板、**自动创建扫码解析规则** |

**明确排除**：打印工位/代理绑定（不需要）、多工位任务下发、B/S 相关一切内容。

**使用规模**：纯内网，1~2 操作用户 + 1 开发调试人员。

---

## 2. 技术选型

| 层 | 选型 | 理由 |
|---|---|---|
| 框架 | .NET 8 + WPF（仅 Windows） | Win11 一流支持，原生观感，启动快 |
| UI 模式 | MVVM：CommunityToolkit.Mvvm + DependencyInjection | 微软官方 MVVM 工具包，源代码生成器减少样板 |
| UI 控件 | 原生 WPF 控件 + 少量自绘（模板编辑器画布） | 不引入重型控件库；如后续需要可加 HandyControl/WPF-UI |
| 数据库 | MySQL 8（服务器端）+ **EF Core 8** + Pomelo.EntityFrameworkCore.MySql | 直连，连接信息由用户正式开始时提供 |
| 密码哈希 | BCrypt.Net-Next | |
| 渲染引擎 | **System.Drawing（GDI+）自研 1-bit 点阵渲染** | 与 GDI 打印同栈；203dpi 下 mm→px 精确换算；与 `agent/render.py` 输出做回归比对 |
| 条码 | Code128 自研（参考 `agent/code128.py` 的编码表） | 68 行逻辑直接翻译，无授权问题 |
| 二维码 | QRCoder（NuGet，MIT） | |
| .lsd 解析 | 自研 C# 解析器（参考 `lsd_probe.py` 的偏移量规格与 `probe_result_8.json` 真值） | 字节级解析规则已知，可逐项对照验证 |
| 打印 | System.Drawing.Printing（GDI 打印）+ RAW（ZPL 预留） | GDI 是 .NET 主场；保留"导出 PNG/PDF"模式供开发机无打印机调试 |
| 字体 | 内置思源黑体/宋体（随 exe 打包） | 避免商用字体授权风险，且保证工位机渲染一致 |
| 日志 | Serilog（本地滚动文件） | 排障用 |
| 打包 | `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` | 官方原生单文件，约 70~120MB；Win11 无需装运行时 |

---

## 3. 架构与目录结构

单项目分层（规模小，不上多项目解决方案）：

```
client/UXLabelshop/                # WPF 工程
├── App.xaml / App.xaml.cs         # 入口、DI 容器、全局异常
├── appsettings.json               # → 运行时生成 config（数据库连接等）
├── Views/                         # XAML 视图
│   ├── LoginWindow.xaml           # 登录
│   ├── MainWindow.xaml            # 主框架（导航 + 页面宿主）
│   ├── ScanPrintPage.xaml         # ① 扫码打印
│   ├── PrintPreviewWindow.xaml    # ② 打印预览（可从扫码页呼出）
│   ├── PrintRecordsPage.xaml      # ③ 打印记录
│   ├── TemplatesPage.xaml         # ④ 模板管理
│   ├── TemplateEditorPage.xaml    # ⑤ 模板编辑器
│   ├── ScanRuleEditDialog.xaml    # 扫码规则查看/微调
│   └── UsersPage.xaml             # 用户管理（管理员）
├── ViewModels/                    # MVVM ViewModel
├── Models/                        # EF Core 实体
├── Services/
│   ├── AuthService.cs             # 登录、会话、权限
│   ├── Db.cs                      # DbContext（连接串运行时可配）
│   ├── TemplateService.cs         # 模板 CRUD、发布
│   ├── ScanRuleService.cs         # 规则匹配、自动生成
│   ├── PrintService.cs            # 任务组装、记录入库
│   ├── Rendering/LabelRenderer.cs # JSON 模板 → 1-bit 点阵（参考 render.py）
│   ├── Rendering/Code128.cs       # （参考 code128.py）
│   ├── Printing/PrintDriver.cs    # GDI 打印 / 文件导出
│   ├── Printing/PrinterProbe.cs   # 枚举打印机、探测纸型/分辨率
│   └── Lsd/LsdImporter.cs         # .lsd → JSON 模板（参考 lsd_probe.py）
├── Controls/                      # 编辑器画布控件、对齐装饰器等
├── Assets/Fonts/                  # 内置字体
└── Tests/                         # 渲染回归比对、lsd 解析对照、规则匹配单测
```

**数据流**：ViewModel → Service（业务+EF Core）→ MySQL；打印走 `LabelRenderer` → `PrintDriver`，同步执行、结果直接入库。

---

## 4. 数据库设计（简化版）

库名建议 `ux_labelshop2`（与旧系统区分），表前缀 `label_`，utf8mb4。用户表前缀 `sys_`。

| 表 | 关键字段 | 说明 |
|---|---|---|
| `sys_user` | id, username, password_hash, role(`user`/`admin`), enabled, created_at | 两级角色，字段内置 role 即可，不再拆 RBAC 五表 |
| `label_template` | id, code, name, width_mm, height_mm, dpi, status(`draft`/`published`/`disabled`), content(JSON), updated_by, updated_at | 模板主体；JSON 含 elements + variables |
| `label_scan_rule` | id, template_id, name, priority, regex, field_map(JSON), enabled | 一个模板多条规则，按优先级匹配 |
| `label_print_record` | id, job_no, template_id, **template_snapshot(JSON)**, user_id, printer_name, copies, total_qty, status, created_at | 模板快照固化，保证历史可复现 |
| `label_print_record_item` | id, record_id, seq, barcode, model, batch, lot, dc, qty, data(JSON) | 每张标签一行，关键字段拆列便于追溯查询 |

与旧 schema 的差异：去掉 role/perm/role_perm/station/event/audit 六张表（两级角色 + 单机打印 + 快照可回溯，不需要它们）；新增 `template_snapshot` 解决无版本表后的历史复现问题。

模板 JSON 结构沿用已验证的格式（mm 坐标 + 203dpi）：

```json
{
  "width_mm": 100.0, "height_mm": 50.0, "dpi": 203,
  "variables": [{"key": "partno", "label": "产品型号", "sample": "UX2115"}],
  "elements": [
    {"type": "text", "x": 1.5, "y": 14.7, "w": 19.18, "h": 4.2, "text": "Part NO：", "size_mm": 3.5},
    {"type": "barcode", "symbology": "code128", "x": 10.24, "y": 2.45, "w": 37.76, "h": 9.75, "text": "{{track}}"},
    {"type": "qrcode", "x": 79.24, "y": 3.57, "size_mm": 14.73, "text": "{{qr1}}"}
  ]
}
```

元素类型：`text` / `barcode` / `qrcode` / `rect` / `line` / `image`。

**首次运行初始化**：exe 启动检测库不存在 → 引导输入连接信息（服务器正式开始时提供）→ 自动建库建表 → 创建默认 admin（首次登录强制改密）→ 提示可导入 .lsd 生成种子模板。

---

## 5. 功能模块设计

### 5.1 登录与权限（功能 1）

- 登录窗：工号 + 密码（BCrypt 校验），可选"记住工号"；支持"记住密码"自动登录（本地 DPAPI 加密存储）。
- 会话：进程内 `Session` 单例，记录当前用户与角色。
- 权限：两级——
  - **用户**：扫码打印、预览打印、查看打印记录；
  - **管理员**：+ 模板编辑/发布/停用、扫码规则调整、新增用户/重置密码、数据库连接设置。
- 界面上按角色隐藏/禁用入口；服务层再做一次角色校验（防内部绕过）。

### 5.2 扫码打印页（功能 2）

主界面（登录后默认页），布局：左=模板选择，中=扫码输入 + 待打印清单表格，右=标签预览。

流程：
1. 选择已发布模板 → 加载变量表与规则；
2. 扫码框自动聚焦，扫码枪回车提交 → 按规则匹配 → 命中则生成一行数据（字段填入对应变量），未命中进入手动录入（保留原始码串用于追溯）；
3. 清单为可编辑 DataGrid：修改单元格、删除行、调整每行打印份数；
4. **空白单元格批量填充**：选中某列（如序列号列）→"批量填充"对话框 → 支持
   - 固定值填充所有空白单元格；
   - 序列号填充（前缀 + 起始值 + 步长 + 位数补零），只填空白单元格、已有值不覆盖；
   - 复制上一行非空值填充；
5. 行选中或光标移动时，右侧实时预览该行标签（调用渲染引擎，所见即所打）；
6. 点击"去打印"进入打印预览。

### 5.3 打印预览（功能 3）

独立窗口：
- **预览区**：全部标签按序分页显示，支持缩放、逐张/连续滚动查看；
- **打印参数**：打印机选择（枚举系统打印机 + 探测分辨率/纸型）、打印份数（整体倍数）、打印后自动保存记录、暂停间隔（可选）；
- **纸张校验**：模板幅面 vs 打印机纸型不匹配时警示；
- 点"开始打印"：逐张渲染 1-bit 点阵 → GDI 送打 → 进度条 → 完成后写 `label_print_record`(+items)；
- 失败处理：单张失败可重试该张，不影响已成功张；记录状态（完成/部分完成/失败）。

### 5.4 打印记录页（功能 4）

- 查询条件：时间段、条码/关键字、模板、操作人；
- 结果表格 + 明细：展开查看该次打印的模板快照、每张明细、参数；
- 双击明细行 → 用快照重新渲染该张标签（可放大核对）；
- 导出 CSV/Excel；
- 初期不做"从记录重打"，后续可加（快照在手，实现容易）。

### 5.5 模板设置（功能 5）

**模板管理页**（管理员）：
- 列表（编码、名称、幅面、状态、更新时间）、新建、复制、编辑、发布（draft→published）、停用；
- 删除仅限 draft 或管理员二次确认（有打印记录引用的模板只允许停用）。

**模板编辑器**（本方案最大的自研 UI 工程）：
- WPF `Canvas` 自研编辑器：元素拖拽、八向缩放手柄、方向键微调（0.1mm）、对齐吸附（网格 0.5mm + 元素边缘对齐）、多层对齐/等距工具；
- 左侧元素工具箱（文本/条码/二维码/矩形/线条/图片），中央画布（mm 标尺 + 幅面边框），右侧属性面板（坐标/尺寸/字号/内容/变量绑定）；
- 变量管理：增删变量、设置样例值；
- 实时预览：画布所见即可切到"打印预览模式"（直接调渲染引擎出真点阵，保证编辑器与实际打印一致）；
- 保存为 draft；发布前强制"试打一张"。

**新增模板两条路**：
1. 空白新建：选幅面（常用幅面 + 自定义 mm）+ DPI，从零编辑；
2. **.lsd 导入**：选 `.lsd` 文件 → 解析幅面/变量/对象 → 生成 JSON 草稿 + 导入报告（可读项/不可读项/需确认项）→ 人工校准后发布。图片 logo 位图读不出，需重新上传。幅面必须用文档结构真值，禁止包围盒估算（沿用既有踩坑结论）。

**自动创建扫码解析规则**（保存/新建模板时）：
- 依据模板变量列表自动生成默认规则草稿：按变量顺序构造分隔符/定长解析的 regex + 字段映射（如 `UX(\d{4})-(\w+)-(\d+)` 三段式，参考既有 8 个模板的原始码结构归纳）；
- 用户可在规则对话框中预览示例匹配效果、调整正则与映射后启用；
- 已有规则冲突（同模板多规则）按优先级排列；
- 目标：新建模板→扫码即能解析，手工配规则只是兜底。

---

## 6. 渲染引擎与打印（参考现有代码重写）

| 参考对象 | 用途 | 验证方式 |
|---|---|---|
| `agent/render.py`（278 行） | 渲染流程、mm→px 换算、1-bit 语义、文本/条码布局规则 | 8 张样张 PNG 像素级回归比对 |
| `agent/code128.py`（68 行） | Code128 编码表 | 逐码对照单测 |
| `agent/printer.py`（336 行） | GDI 打印参数、自定义纸型、DPI 设置、RAW 输出思路 | 开发机 `mode=file` 导出验证 + 真机现场验收 |
| `lsd_probe.py`（284 行） | `.lsd` 字节偏移规格 | `probe_result_8.json` 真值对照单测 |

关键约束：
- 渲染统一 203dpi（`px = mm / 25.4 * dpi`，四舍五入规则与参考实现一致）；
- 1-bit 位图 → GDI 打印保证 1:1 无缩放；
- C#（GDI+）与 Python（PIL）字体度量存在差异，回归比对时允许亚像素级偏差，但条码模块宽度、二维码定位必须一致（这是扫描可靠性的关键）；
- 开发机无 Citizen CL-F3404：打印走"文件导出 PNG/PDF"模式验证，真机列入现场验收。

---

## 7. 打包与部署

- 发布命令：`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
- 产物：单 exe（含字体与资源，预计 70~120MB）；
- 首次运行引导配置数据库连接（连接信息存本地配置，DPAPI 加密密码）；
- 现场部署：exe + 打印机驱动即可；日志在 exe 同目录 `logs/`。

---

## 8. 开发阶段计划

| 阶段 | 内容 | 交付物 / DoD |
|---|---|---|
| **W0 骨架** | WPF 工程 + DI/MVVM 框架 + 登录/权限/用户管理 + EF Core 建库初始化 | exe 能登录、建库、管用户 |
| **W1 渲染与打印内核** | LabelRenderer + Code128 + QR + PrintDriver（含导出模式）+ 样张回归测试 | 8 张样张比对达标；能导出 PNG/PDF |
| **W2 扫码打印主流程** | 扫码打印页 + 规则匹配 + 批量填充 + 打印预览窗口 + 打印入库 | 扫码 → 填充 → 预览 → 打印（file 模式）→ 记录入库闭环 |
| **W3 模板编辑器** | Canvas 编辑器全套 + 模板管理 + 发布流程 | 管理员能纯界面画模板并打印 |
| **W4 lsd 导入与规则自动生成** | LsdImporter + 导入报告 + 规则自动生成与调整 | 8 个 .lsd 导入试打与样张一致 ≥95%；新模板自动出规则 |
| **W5 打印记录与收尾** | 记录查询/明细/导出 + 真机打印参数适配 + 打包与现场文档 | 单 exe 干净机器可运行，现场验收清单 |

顺序理由：渲染内核先行（W1），因为预览、打印、编辑器实时预览都依赖它，且风险最高（像素一致性），早做早暴露。

---

## 9. 风险与应对

| 风险 | 影响 | 应对 |
|---|---|---|
| 渲染引擎字体度量与 PIL 不一致 | 样张比对不过、条码不可扫 | 条码/二维码模块必须严格一致；文本允许亚像素偏差；W1 单独回归 |
| WPF 自研编辑器工程量超预期 | W3 拖期 | 编辑器先做最小集（拖/缩放/属性面板/吸附），高级功能（多选、旋转）后置 |
| 规则自动生成覆盖不全 | 部分码解析不了 | 兜底：手动录入 + 规则可手工调整 |
| 开发机无 CL-F3404 驱动 | 无法验证真机 | file 模式验证 + 现场验收清单（纸型/浓度/走纸校准） |
| `.lsd` 样式/条码类型读不全 | 导入模板与原版有差异 | 导入即草稿 + 报告 + 人工校准，目标 95% 一致 |
| 数据库凭据存客户端 | 内网低危 | 专用低权限账号（仅本库读写）；密码 DPAPI 加密存本地 |
| 中文字体授权 | 法律风险 | 内置思源黑体/宋体 |

---

## 10. 待确认事项

1. **模板版本管理**：本方案用"打印记录存模板快照"替代版本表（更简单）。是否接受？若希望模板本身可回滚历史版本，加一张 `label_template_ver` 即可，工作量 +2~3 天。
2. **打印份数语义**：清单中每行份数 × 预览窗口整体份数，两者相乘，是否符合预期？
3. **Excel 导出**：打印记录导出用 CSV（零依赖）还是真 Excel（EPPlus/ClosedXML，需引入库）？
4. **.lsd 导入是否第一期就要**：可以后置到 W4（当前计划内）。若现场急于迁移存量模板，可提前。
5. **用户管理**：管理员在客户端内管理用户（新增/重置密码/停用），仅此而已，对吗？
6. **旧目录处置**：server/、web/、agent/ 保留作参考（本方案多次引用），client/ 起步后不同步维护旧代码，是否接受？

---

## 11. 下一步

方案确认后启动 W0：
1. 创建 `client/UXLabelshop/` WPF 工程，搭 DI + MVVM 骨架；
2. EF Core 实体 + 初始化建库；
3. 登录窗 + 用户管理跑通；
4. 首次 `dotnet publish` 单文件验证。

**当前不执行任何编码，等待方案确认。**
