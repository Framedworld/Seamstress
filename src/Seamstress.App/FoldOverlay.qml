import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Internal lines in the 2D view (plain lines, mountain and valley folds), what the Internal Line, Dart and Pleat tools
// are drawing, and the panel with those tools' settings. It sits over the pattern without taking the mouse, except
// on the panel itself.
Item {
    id: overlay
    // The PatternView: its scene and its world-to-screen mapping.
    property Item view
    anchors.fill: parent

    function tr(text) { App.languageRevision; return App.tr(text) }
    readonly property var scene: view ? view.scene : ({})
    readonly property color mountain: "#FF8C42"
    readonly property color valley: "#3DA5F4"
    readonly property color plain: Qt.alpha(palette.windowText, 0.65)

    onSceneChanged: lines.requestPaint()
    Connections {
        target: overlay.view
        function onZoomChanged() { lines.requestPaint() }
        function onPanXChanged() { lines.requestPaint() }
        function onPanYChanged() { lines.requestPaint() }
        function onWidthChanged() { lines.requestPaint() }
        function onHeightChanged() { lines.requestPaint() }
    }

    Canvas {
        id: lines
        anchors.fill: parent
        renderStrategy: Canvas.Cooperative

        function trace(ctx, pts) {
            ctx.beginPath()
            for (let i = 0; i < pts.length; i += 2) {
                const x = overlay.view.sx(pts[i]), y = overlay.view.sy(pts[i + 1])
                if (i === 0) ctx.moveTo(x, y)
                else ctx.lineTo(x, y)
            }
        }

        // Mountain folds dash-dot in orange, valley folds dashed in blue, plain lines finely dashed.
        function styleFor(ctx, fold, valley) {
            if (fold <= 0.5) { ctx.strokeStyle = overlay.plain; ctx.setLineDash([3, 3]) }
            else if (valley) { ctx.strokeStyle = overlay.valley; ctx.setLineDash([6, 4]) }
            else { ctx.strokeStyle = overlay.mountain; ctx.setLineDash([9, 3, 2, 3]) }
        }

        onPaint: {
            const ctx = getContext("2d")
            ctx.reset()
            const s = overlay.scene
            if (!overlay.view || s.mode === "uv")
                return
            for (const line of s.internalLines || []) {
                if (line.selected) {
                    ctx.setLineDash([])
                    ctx.strokeStyle = Qt.alpha(palette.highlight, 0.45)
                    ctx.lineWidth = 7
                    trace(ctx, line.points); ctx.stroke()
                }
                styleFor(ctx, line.fold, line.valley)
                ctx.lineWidth = line.fold > 0.5 ? 2 : 1.5
                trace(ctx, line.points); ctx.stroke()
            }
            ctx.setLineDash([])

            if (s.lineDraft) {
                styleFor(ctx, s.lineDraftFold, s.lineDraftValley)
                ctx.lineWidth = 2
                trace(ctx, s.lineDraft); ctx.stroke()
                ctx.setLineDash([])
                ctx.fillStyle = palette.highlight
                const d = s.lineDraft
                for (const i of [0, d.length - 2])
                    ctx.fillRect(overlay.view.sx(d[i]) - 3, overlay.view.sy(d[i + 1]) - 3, 6, 6)
            }
            if (s.dartDraft) {
                // The wedge that will be cut out: shaded once both legs are there, its legs drawn as the seam.
                const d = s.dartDraft
                ctx.strokeStyle = palette.highlight
                ctx.lineWidth = 2
                trace(ctx, d)
                if (d.length >= 6) {
                    ctx.closePath()
                    ctx.fillStyle = Qt.alpha(palette.highlight, 0.25)
                    ctx.fill()
                }
                ctx.stroke()
                ctx.fillStyle = palette.highlight
                for (let i = 0; i < d.length; i += 2) {
                    ctx.beginPath(); ctx.arc(overlay.view.sx(d[i]), overlay.view.sy(d[i + 1]), 3.5, 0, Math.PI * 2); ctx.fill()
                }
            }
            if (s.pleatDraft) {
                const p = s.pleatDraft
                const bad = p.problem !== undefined
                ctx.strokeStyle = bad ? "#e5484d" : palette.highlight
                ctx.lineWidth = 5
                trace(ctx, p.span); ctx.stroke()
                ctx.lineWidth = 2
                for (const fold of p.lines) {
                    styleFor(ctx, 1, fold.valley)
                    trace(ctx, fold.points); ctx.stroke()
                }
                ctx.setLineDash([])
                // The read-out by the end of the stretch: how many, how deep, over how much edge.
                const unit = App.imperial ? 2.54 : 1, name = App.imperial ? " in" : " cm"
                const text = bad ? p.problem
                    : overlay.tr("%1 pleats, %2 deep, over %3").arg(p.count).arg((p.depth / unit).toFixed(1) + name).arg((p.width / unit).toFixed(1) + name)
                const end = p.span.length - 2
                ctx.font = "bold 11px sans-serif"
                const w = ctx.measureText(text).width + 12
                const x = overlay.view.sx(p.span[end]) + 10, y = overlay.view.sy(p.span[end + 1]) - 22
                ctx.fillStyle = Qt.rgba(0.07, 0.07, 0.09, 0.88)
                ctx.beginPath(); ctx.roundedRect(x, y, w, 18, 4, 4); ctx.fill()
                ctx.fillStyle = "white"
                ctx.textAlign = "left"
                ctx.fillText(text, x + 6, y + 13)
            }
        }
    }

    // Settings of the Internal Line and Pleat tools.
    Pane {
        id: panel
        readonly property var tool: overlay.scene.foldTool
        readonly property bool lineTool: overlay.scene.tool === "InternalLine"
        readonly property real unit: App.imperial ? 2.54 : 1
        visible: tool !== undefined && overlay.scene.mode !== "uv" && overlay.scene.tool !== "Dart"
        anchors { right: parent.right; top: parent.top; margins: 8 }
        padding: 10
        width: 240
        background: Rectangle {
            color: palette.window
            border.color: Qt.alpha(palette.windowText, 0.2)
            radius: 6
            // Clicks on the panel stay on the panel.
            MouseArea { anchors.fill: parent; acceptedButtons: Qt.AllButtons }
        }
        ColumnLayout {
            width: panel.availableWidth
            spacing: 6
            Label {
                font.bold: true
                text: !panel.lineTool ? overlay.tr("Pleats")
                    : panel.tool && panel.tool.selected ? (panel.tool.angle > 0.5 ? overlay.tr("Selected fold line") : overlay.tr("Selected line"))
                    : overlay.tr("New line")
            }
            GridLayout {
                visible: panel.lineTool
                columns: 3
                columnSpacing: 6
                rowSpacing: 4
                Layout.fillWidth: true
                Label { text: overlay.tr("Fold angle") }
                NumberField {
                    Layout.fillWidth: true
                    implicitHeight: 26
                    padding: 3
                    live: true
                    from: 0; to: 180; decimals: 0; step: 5
                    value: panel.tool ? panel.tool.angle : 0
                    onEdited: (v) => App.foldAngle = v
                }
                Label { text: "°"; opacity: 0.6 }
                Label { text: overlay.tr("Strength") }
                NumberField {
                    Layout.fillWidth: true
                    implicitHeight: 26
                    padding: 3
                    live: true
                    enabled: panel.tool && panel.tool.angle > 0.5
                    from: 0; to: 100; decimals: 0; step: 5
                    value: panel.tool ? panel.tool.strength * 100 : 70
                    onEdited: (v) => App.foldStrength = v
                }
                Label { text: "%"; opacity: 0.6 }
            }
            RowLayout {
                visible: panel.lineTool
                spacing: 6
                CheckBox {
                    text: overlay.tr("Valley fold")
                    enabled: panel.tool && panel.tool.angle > 0.5
                    checked: panel.tool ? panel.tool.valley : false
                    onToggled: App.foldValley = checked
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: overlay.tr("A mountain fold makes a ridge on the outside of the garment; a valley fold makes a groove")
                }
            }
            GridLayout {
                visible: !panel.lineTool
                columns: 3
                columnSpacing: 6
                rowSpacing: 4
                Layout.fillWidth: true
                Label { text: overlay.tr("Count") }
                NumberField {
                    Layout.fillWidth: true
                    implicitHeight: 26
                    padding: 3
                    live: true
                    from: 1; to: 40; decimals: 0; step: 1
                    value: panel.tool ? panel.tool.count : 4
                    onEdited: (v) => App.pleatCount = Math.round(v)
                }
                Item { implicitWidth: 1 }
                Label { text: overlay.tr("Depth") }
                NumberField {
                    Layout.fillWidth: true
                    implicitHeight: 26
                    padding: 3
                    live: true
                    from: 0.2; decimals: 1; step: App.imperial ? 0.25 : 0.5
                    value: panel.tool ? panel.tool.depth / panel.unit : 3
                    onEdited: (v) => App.pleatDepth = v * panel.unit
                }
                Label { text: App.imperial ? "in" : "cm"; opacity: 0.6 }
                Label {
                    text: overlay.tr("Stitched down")
                    ToolTip.visible: stitchMouse.containsMouse
                    ToolTip.delay: 500
                    ToolTip.text: overlay.tr("How far down from the edge each pleat is sewn shut. Below that the pleats open. 0 only tacks them at the edge; the garment then needs a waistband to hold it up.")
                    MouseArea { id: stitchMouse; anchors.fill: parent; hoverEnabled: true }
                }
                NumberField {
                    Layout.fillWidth: true
                    implicitHeight: 26
                    padding: 3
                    live: true
                    from: 0; decimals: 1; step: App.imperial ? 0.5 : 1
                    value: panel.tool && panel.tool.stitchDown !== undefined ? panel.tool.stitchDown / panel.unit : 8
                    onEdited: (v) => App.pleatStitchDown = v * panel.unit
                }
                Label { text: App.imperial ? "in" : "cm"; opacity: 0.6 }
            }
            Button {
                visible: panel.lineTool && panel.tool !== undefined && panel.tool.selected
                Layout.alignment: Qt.AlignRight
                text: overlay.tr("Delete line")
                onClicked: { App.deleteInternalLine(); overlay.view.forceActiveFocus() }
            }
        }
    }
}
