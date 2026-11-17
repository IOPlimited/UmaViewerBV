# Live 分轨剪辑兼容性

调研日期：2026-11-07。目标工具：VEGAS、Premiere、After Effects、FL Studio、UTAU / OpenUtau。本文区分已经确认的音频要求与建议增加的交付方式；当前未改动渲染实现。

## 1. 音频与轨道处理的边界

已确认的 vocal 规则：每个有效歌唱绑定的每条 wave 独立建轨，名称包含 Live 编号、实际声线角色名、歌唱位置和 wave 顺序。所有轨道从同一零点开始，保持完整演出长度；未使用区间静音，不压缩时间。

使用区间保留解码后的采样幅度与原有声道关系。播放器音量、合唱平衡和声像保存为独立轨道参数与自动化，导入后再作用于整条轨道，不预先烘焙进 vocal。源素材已经包含的混响、叠唱等录音效果继续保留。

主混音独立按现有 Live 路径生成，包含原有的音量、声像和所有实际使用声源。MKV 以它作为默认音轨，MP4 同样保存双声道主混音。

单个角色的全部 wave 应在恢复对应轨道处理后，与当前阵容下隔离该角色的演唱比较。未加载自动化的 WAV 直接相加，响度和声像会不同；也不将多角色阵容的混音参数重新计算成一人阵容。

## 2. 最大兼容性的交付建议

保留已确认的 MKV / MP4 输出选择，同时增加可选的独立素材包：

| 内容 | 建议格式 | 用途 |
| --- | --- | --- |
| 多轨视频 | MKV；既定视频编码 + 48 kHz、FLAC 24-bit 音轨 | 播放、归档和分轨封装；默认总混音 |
| 播放参考 | 选择 MP4 时使用 H.264 + AAC 320 kbps 双声道 | 剪辑软件中参考画面和主混音；仍保留现有 HEVC 选择，H.264 作为兼容性优先选项 |
| 独立音频 | 每轨一个 PCM 24-bit WAV，统一 48 kHz、零点和采样长度 | VEGAS、PR、AE、FL Studio 的共同素材入口；从同一解码支路生成，不经 AAC 转换 |
| 轨道清单与处理 | UTF-8 JSON，另附便于查看的 CSV | 保存身份、槽位、wave ID、静音区间、音量和声像，供后续软件适配读取 |

素材包是建议新增选项，不要求一次输出两份视频。选择 MKV 时，兼容性较低的软件可使用独立 WAV，画面需要时将既有 H.264 视频流复制封装成 MP4；视频流不必重新编码，播放音轨可单独转 AAC。

WAV 与 MKV 中的 FLAC 可以保存相同的 24-bit PCM。FLAC 的无损是相对于送入编码器的 PCM；解码 float 转 24-bit、采样率转换、单声道转双声道仍是独立处理步骤。编辑用 vocal WAV 建议保留源声道数，单声道素材交给软件的单声道轨道；MKV 继续按既定双声道规则封装。若源采样率不同，统一 48 kHz 会发生重采样，清单应记录源参数与输出参数。

## 3. 各软件兼容性与限制

下表依据官方文档和开发团队公告；软件内的实际导入及曲线恢复在实现阶段按具体版本验证。

| 软件 | 共同素材入口 | 可编辑音量与声像 | 限制与使用方式 |
| --- | --- | --- | --- |
| VEGAS Pro | 独立 WAV + MP4 画面 | 原生轨道包络；脚本 API 可创建 Volume、Pan 包络 | 官方 Pro 21 帮助将 MKV Reader 标为 experimental；不能据此承诺所有版本直接读入 FLAC 多轨 MKV。使用独立 WAV 最稳定。自动化可通过专用脚本恢复 |
| Premiere | 独立 WAV + H.264 MP4 | Audio Track Mixer 支持音量、声像自动化 | 25.2 起增加 H.264 MKV 支持，官方发布说明列 H.264/AAC；团队公告还列 PCM。不能把该支持外推为 HEVC + FLAC 多轨组合。JSON/CSV 曲线不是原生轨道自动化导入格式，需专用适配 |
| After Effects | 独立 WAV + H.264 MP4 | 图层 Audio Levels 与 Stereo Mixer 等效果可编辑、可设关键帧 | 25.2 的 H.264 MKV 支持不等于任意 FLAC MKV。音频按图层而非 DAW Mixer 轨组织；需把处理映射为图层参数/效果，不能直接照搬 PR 的轨道模型 |
| FL Studio | WAV；亦支持 FLAC 素材 | Mixer 参数与 Automation Clips | 每条需独立控制的 wave 应分配独立 Mixer Insert。Playlist 上分行不自动保证独立混音路由。不要随工程 BPM 对音频做伸缩；JSON/CSV 不会自动变成 Automation Clips |
| 传统 UTAU | 用于进一步采样制作的音频素材 | 不作为本方案的多轨混音与视频回放目标 | 整曲 wave 不包含 UST 音符、歌词/音素或 oto.ini 标注，不能直接作为可重唱音库。传统 resampler 链路的采样格式需按所用工具另行转换，不能将统一 48 kHz/24-bit 分轨当作全部工具的通用输入 |
| OpenUtau | 官方支持导入 WAV、MP3、OGG、FLAC 参考音频 | 有基础混音功能 | 可导入本方案 WAV 作音频参考；它与传统 UTAU 分开评估。官方定位仍以歌声合成为主，不能将普通音频导入等同于导入 DAW 的完整音量/声像自动化工程，也不会自动生成可编辑歌声音符 |

依据：

- [VEGAS File I/O（Pro 21）](https://help.magix-hub.com/video/vegas/en/content/topics/preffileio.htm)、[Pro 23 音频轨道自动化](https://help.magix-hub.com/video/vegas/23/en/content/topics/11-autoscript/insertaudioenv.htm)、[Pro 22 包络脚本 API](https://help.magix-hub.com/video/vegas/22/en/content/topics/external/scriptfaq_envelopes.htm)。版本各异，分别说明文件读取、轨道功能和脚本能力。
- [Premiere 发布说明](https://helpx.adobe.com/premiere/desktop/whats-new/release-notes.html)、[Adobe 团队的 H.264 MKV 公告](https://community.adobe.com/announcements-732/now-released-support-for-h-264-in-mkv-311714)、[Premiere 支持格式](https://helpx.adobe.com/hk_en/premiere/desktop/organize-media/import-files/supported-file-formats.html)、[Audio Track Mixer](https://helpx.adobe.com/premiere/desktop/add-audio-effects/advanced-audio-techniques/about-audio-track-mixer.html)。
- [After Effects 支持格式](https://helpx.adobe.com/after-effects/desktop/get-started/supported-file-formats/supported-file-formats.html)、[音频效果](https://helpx.adobe.com/sa_en/after-effects/desktop/apply-effects-and-animation-presets/list-of-effects/audio-effects.html)。
- [FL Studio 采样器与 Audio Clip](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/chansettings_sampler.htm)、[Mixer](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/mixer.htm)、[音频格式与位深](https://www.image-line.com/fl-studio-learning-content/fl-studio-online-manual/html/fformats_save_export.htm)。
- [UTAU 作者页面](https://utau2008.xrea.jp/)、[OpenUtau 音频导入](https://github.com/openutau/OpenUtau/wiki/Getting-Started)、[OpenUtau 项目定位](https://github.com/openutau/OpenUtau/blob/master/README.md)。

## 4. 自动化不能通过普通媒体文件通用恢复

WAV 保存音频，不是多轨混音工程。MKV 支持轨道标签和附件，可以携带处理数据，但这些软件没有共同的“读取 MKV/WAV 后自动建立音量、声像包络”约定。Matroska 也说明自定义标签的互操作范围有限。[Matroska 标签规范](https://www.matroska.org/technical/tagging.html)

因此，最大兼容性的共同能力是“正确导入、对齐和独立编辑声音”。要一键恢复可编辑混音，还需分别映射为 VEGAS 包络、PR 轨道自动化、AE 图层关键帧、FL Studio 自动化。JSON/CSV 是原始交换数据，保存它本身不等于已经完成这些导入器。

AAF、OMF、FCP XML 各有用途，但不能据此得到覆盖以上全部工具的统一自动化工程；本轮不选择其中一个作为所有软件的强制入口。

### 曲线最小保存内容

- 稳定轨道 ID、Live ID、出演与声线角色 ID、Binding.Slot、位置名、资源名、bank 顺序及实际 wave ID。
- 视频零点、输出采样率和长度；区间以整数采样索引表示，同时提供秒数。
- selector、使用门控与变化原因；音量的线性值、声像原值、变化时刻和保持/插值方式。零增益单独表示，避免将其写成无效的负无穷 JSON 数字。
- 源声道数与原采样率、输出声道映射、增益与声像的处理顺序。保留原始 Unity 参数，软件适配时再转换为 dB、平衡或声像控制。

不同软件的 pan law、单声道声像、立体声 balance 与 stereo panner 不完全相同。例如 FL Studio 默认使用 Circular Panning Law，PR 区分声像和平衡。不能只复制同一个旋钮数值就假定与 Unity 听感相同。[FL Studio Mixer](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/mixer.htm)、[PR 声像与平衡](https://helpx.adobe.com/premiere/desktop/add-audio-effects/apply-audio-effects/pan-or-balance-a-stereo-track.html)

实现时以 Unity 主混音和隔离角色的参考输出核对处理映射。若软件原生旋钮不能表达同一处理，可在该软件轨道上使用可编辑的通道增益/矩阵效果；不通过提前改写素材来补偿。

## 5. “静音”与“可取消静音”的区别

| 交付内容 | 保留完整时间轴 | 能恢复 Live 未使用的人声 |
| --- | --- | --- |
| 使用区间保留采样、其他区间写零的 WAV/FLAC | 是 | 不能，零采样中没有原人声 |
| 未静音源 wave + 使用区间/门控数据 | 是 | 可以，由软件的静音包络或片段安排控制 |

当前已确认的播放分轨采用第一行。若“静音不是删除”还要求导入后能取消静音，建议素材包额外保留第二行，按源资源与 wave 去重保存，通过轨道清单让多个槽位引用同一源。仅恢复音量/声像自动化不能恢复已经写零的人声。

## 6. 实施边界

已确认：MKV 默认主混音与多轨、MP4 双声道主混音、绑定 × wave、未使用区间静音、不烘焙 vocal 增益与声像、既定命名及附加声音规则。

建议纳入：独立 PCM WAV 素材包，轨道清单与 JSON/CSV 自动化；这是覆盖所列剪辑软件的通用交付基础。

另需确定的能力：是否同时保存未静音源 wave，以支持取消静音；软件专用的一键自动化导入器是否纳入首轮。各软件的通用 WAV 导入能力与自动化恢复应分别验收。
