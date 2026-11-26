import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Properties of the current selection: topology, arrangement, fabric physics, material, UVs, seams
// and the simulation settings.
Pane {
    id: root
    padding: 0
    background: Rectangle { color: palette.window }

    function tr(text) { App.languageRevision; return App.tr(text) }
    property var sel: ({ kind: "none", count: 0, presets: [], materials: [], simulation: {} })
    signal pickColor(string current)
    signal pickTexture()

    // Values are stored metric; with imperial units they are converted for display and converted back when edited.
    readonly property bool imperial: App.imperial
    readonly property real cmUnit: imperial ? 2.54 : 1            // centimetres per displayed length unit
    readonly property real mUnit: imperial ? 0.0254 : 1           // metres per displayed position unit
    readonly property real mmUnit: imperial ? 25.4 : 1            // millimetres per displayed thickness unit
    readonly property real gsmUnit: imperial ? 33.9057 : 1        // g/m² per displayed fabric weight unit (oz/yd²)
    readonly property string lengthName: imperial ? "in" : "cm"

    function refresh() { sel = JSON.parse(App.selection()) }
    Component.onCompleted: refresh()
    Connections {
        target: App
        function onSelectionRevisionChanged() { Qt.callLater(root.refresh) }
    }

    // "--view propsbottom" (used when capturing documentation screenshots) starts scrolled to the end.
    // "--view propsat40" starts scrolled 40% of the way down, to capture the sections in the middle.
    Timer {
        interval: 2000
        running: /props(bottom|at\d+)/.test(App.startupView)
        onTriggered: {
            const at = /propsat(\d+)/.exec(App.startupView)
            const end = 1 - scroll.ScrollBar.vertical.size
            scroll.ScrollBar.vertical.position = Math.max(0, at ? Math.min(end, end * Number(at[1]) / 100) : end)
        }
    }

    component Section: Rectangle {
        id: section
        property string title: ""
        property bool open: true
        default property alias content: body.data
        Layout.fillWidth: true
        Layout.leftMargin: 8
        Layout.rightMargin: 8
        Layout.topMargin: 6
        implicitHeight: sectionLayout.implicitHeight
        radius: 6
        color: palette.alternateBase
        border.color: Qt.alpha(palette.mid, 0.88)
        border.width: 1

        ColumnLayout {
            id: sectionLayout
            anchors { left: parent.left; right: parent.right; top: parent.top }
            spacing: 0

            Rectangle {
                Layout.fillWidth: true
                implicitHeight: 28
                radius: 6
                color: headerMouse.containsMouse ? Qt.alpha(palette.windowText, 0.05) : "transparent"
                RowLayout {
                    anchors { fill: parent; leftMargin: 10; rightMargin: 10 }
                    spacing: 6
                    Label {
                        text: section.open ? "▾" : "▸"
                        font.pixelSize: 11
                        color: section.open ? palette.highlight : palette.placeholderText
                    }
                    Label {
                        Layout.fillWidth: true
                        elide: Text.ElideRight
                        text: section.title
                        font.pixelSize: 12
                        font.weight: Font.DemiBold
                        color: palette.windowText
                    }
                }
                MouseArea {
                    id: headerMouse
                    anchors.fill: parent
                    hoverEnabled: true
                    cursorShape: Qt.PointingHandCursor
                    onClicked: section.open = !section.open
                }
            }

            Rectangle {
                visible: section.open
                Layout.fillWidth: true
                Layout.leftMargin: 1
                Layout.rightMargin: 1
                implicitHeight: 1
                color: Qt.alpha(palette.mid, 0.75)
            }

            ColumnLayout {
                id: body
                visible: section.open
                Layout.fillWidth: true
                Layout.margins: 10
                spacing: 7
            }
        }
    }

    // A labelled number box: type a value, or drag in the box to change it (see NumberField).
    component NumberRow: RowLayout {
        id: numberRow
        property string label: ""
        property real value: 0
        property int decimals: 2
        property real step: 1
        property real from: -1e9
        property string suffix: ""
        // Shown when the pointer rests on the row.
        property string tip: ""
        signal edited(real value)
        Layout.fillWidth: true
        spacing: 6
        HoverHandler { id: numberHover }
        ToolTip.visible: numberRow.tip !== "" && numberHover.hovered
        ToolTip.delay: 500
        ToolTip.text: numberRow.tip
        Label { text: numberRow.label; Layout.preferredWidth: 112; elide: Text.ElideRight; font.pixelSize: 12; color: palette.windowText; opacity: 0.82 }
        NumberField {
            Layout.fillWidth: true
            implicitHeight: 24
            value: numberRow.value
            decimals: numberRow.decimals
            step: numberRow.step
            from: numberRow.from
            onEdited: (v) => numberRow.edited(v)
        }
        Label { visible: numberRow.suffix !== ""; text: numberRow.suffix; font.pixelSize: 11; color: palette.placeholderText; Layout.preferredWidth: 26 }
    }

    component SliderRow: RowLayout {
        id: sliderRow
        property string label: ""
        property real value: 0
        property real from: 0
        property real to: 100
        property real stepSize: 1
        property int decimals: 0
        property string suffix: ""
        property int valueWidth: 54
        // Shown when the pointer rests on the row.
        property string tip: ""
        signal edited(real value)
        Layout.fillWidth: true
        spacing: 6
        HoverHandler { id: sliderHover }
        ToolTip.visible: sliderRow.tip !== "" && sliderHover.hovered
        ToolTip.delay: 500
        ToolTip.text: sliderRow.tip
        onValueChanged: if (!slider.pressed) slider.value = value
        Label { text: sliderRow.label; Layout.preferredWidth: 112; elide: Text.ElideRight; font.pixelSize: 12; color: palette.windowText; opacity: 0.82 }
        Slider {
            id: slider
            Layout.fillWidth: true
            implicitHeight: 22
            from: sliderRow.from; to: sliderRow.to; stepSize: sliderRow.stepSize
            value: sliderRow.value
            onMoved: sliderRow.edited(value)
            background: Rectangle {
                x: slider.leftPadding
                y: slider.topPadding + slider.availableHeight / 2 - height / 2
                implicitWidth: 100
                implicitHeight: 4
                width: slider.availableWidth
                height: implicitHeight
                radius: 2
                color: palette.base
                border.color: Qt.alpha(palette.mid, 0.75)
                border.width: 1
                Rectangle {
                    width: slider.visualPosition * parent.width
                    height: parent.height
                    radius: 2
                    color: slider.enabled ? palette.highlight : palette.mid
                }
            }
            handle: Rectangle {
                x: slider.leftPadding + slider.visualPosition * (slider.availableWidth - width)
                y: slider.topPadding + slider.availableHeight / 2 - height / 2
                implicitWidth: 12
                implicitHeight: 12
                radius: 6
                color: slider.pressed || slider.hovered ? palette.brightText : palette.windowText
                border.color: slider.pressed || slider.hovered ? palette.highlight : Qt.alpha(palette.highlight, 0.85)
                border.width: slider.pressed ? 2.5 : 1.5
            }
        }
        Label {
            text: slider.value.toFixed(sliderRow.decimals) + sliderRow.suffix
            Layout.preferredWidth: sliderRow.valueWidth
            horizontalAlignment: Text.AlignRight
            font.pixelSize: 11
            color: palette.windowText
            opacity: 0.82
        }
    }

    component InfoRow: RowLayout {
        property string label: ""
        property string value: ""
        Layout.fillWidth: true
        Label { text: parent.label; Layout.preferredWidth: 112; font.pixelSize: 12; color: palette.windowText; opacity: 0.78 }
        Label { text: parent.value; Layout.fillWidth: true; font.pixelSize: 11; elide: Text.ElideRight; horizontalAlignment: Text.AlignRight; color: palette.windowText; opacity: 0.92 }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 0

        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 34
            color: palette.window
            Rectangle {
                anchors { left: parent.left; right: parent.right; bottom: parent.bottom }
                height: 1
                color: palette.mid
            }
            Label {
                anchors { left: parent.left; right: parent.right; verticalCenter: parent.verticalCenter; leftMargin: 12; rightMargin: 12 }
                font.pixelSize: 12
                font.weight: Font.DemiBold
                elide: Text.ElideRight
                text: root.sel.kind === "seam" ? tr("Seam")
                    : root.sel.kind === "reference" ? root.sel.reference.name
                    : root.sel.kind === "pieces" ? root.sel.piece.name
                    : root.sel.kind === "garment" ? root.sel.garment.name
                    : tr("Properties")
            }
        }

        ScrollView {
            id: scroll
            Layout.fillWidth: true
            Layout.fillHeight: true
            contentWidth: availableWidth
            clip: true

            ColumnLayout {
                width: scroll.availableWidth
                spacing: 0

                Label {
                    visible: root.sel.kind === "none"
                    Layout.fillWidth: true
                    padding: 14
                    wrapMode: Text.WordWrap
                    font.pixelSize: 12
                    color: palette.placeholderText
                    text: tr("Nothing selected. Add a garment from the Garment menu or draw a piece in the 2D view.")
                }

            // ---- Reference image -----------------------------------------------------
            Section {
                title: tr("Reference Image")
                visible: root.sel.kind === "reference"
                InfoRow {
                    label: tr("File")
                    value: root.sel.reference ? (root.sel.reference.missing ? "⚠ " + tr("missing:") + " " : "") + root.sel.reference.file : ""
                }
                Label {
                    visible: root.sel.reference ? root.sel.reference.missing : false
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    color: App.themeWarningText
                    font.pixelSize: 11
                    text: root.sel.reference ? root.sel.reference.path : ""
                }
                CheckBox {
                    text: tr("Lock Reference")
                    checked: root.sel.reference ? root.sel.reference.locked : false
                    onToggled: if (root.sel.reference) App.setReferenceLocked(root.sel.reference.id, checked)
                }
                Label {
                    visible: root.sel.reference ? root.sel.reference.locked : false
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.6
                    font.pixelSize: 11
                    text: tr("Locked references stay in place. Unlock to move or resize them.")
                }
                NumberRow {
                    label: tr("Position X"); suffix: root.lengthName
                    enabled: root.sel.reference ? !root.sel.reference.locked : false
                    decimals: root.imperial ? 2 : 1; step: root.imperial ? 0.5 : 1
                    value: (root.sel.reference ? root.sel.reference.x : 0) / root.cmUnit
                    onEdited: (v) => App.setReferenceValue("x", v * root.cmUnit)
                }
                NumberRow {
                    label: tr("Position Y"); suffix: root.lengthName
                    enabled: root.sel.reference ? !root.sel.reference.locked : false
                    decimals: root.imperial ? 2 : 1; step: root.imperial ? 0.5 : 1
                    value: (root.sel.reference ? root.sel.reference.y : 0) / root.cmUnit
                    onEdited: (v) => App.setReferenceValue("y", v * root.cmUnit)
                }
                NumberRow {
                    label: tr("Width"); suffix: root.lengthName
                    enabled: root.sel.reference ? !root.sel.reference.locked : false
                    decimals: root.imperial ? 2 : 1; step: root.imperial ? 0.5 : 1; from: 0.1
                    value: (root.sel.reference ? root.sel.reference.width : 0) / root.cmUnit
                    onEdited: (v) => App.setReferenceValue("width", v * root.cmUnit)
                }
                NumberRow {
                    label: tr("Height"); suffix: root.lengthName
                    enabled: root.sel.reference ? !root.sel.reference.locked : false
                    decimals: root.imperial ? 2 : 1; step: root.imperial ? 0.5 : 1; from: 0.1
                    value: (root.sel.reference ? root.sel.reference.height : 0) / root.cmUnit
                    onEdited: (v) => App.setReferenceValue("height", v * root.cmUnit)
                }
                NumberRow {
                    label: tr("Rotation"); suffix: "°"; decimals: 1; step: 5
                    enabled: root.sel.reference ? !root.sel.reference.locked : false
                    value: root.sel.reference ? root.sel.reference.rotation : 0
                    onEdited: (v) => App.setReferenceValue("rotation", v)
                }
                SliderRow {
                    label: tr("Opacity"); from: 0; to: 1; stepSize: 0.05; decimals: 2
                    value: root.sel.reference ? root.sel.reference.opacity : 0
                    onEdited: (v) => App.setReferenceValue("opacity", v)
                }
                CheckBox {
                    text: tr("Show Reference")
                    checked: root.sel.reference ? root.sel.reference.visible : false
                    onToggled: if (root.sel.reference) App.setReferenceVisible(root.sel.reference.id, checked)
                }
                Button {
                    Layout.fillWidth: true
                    text: tr("Remove Reference")
                    enabled: root.sel.reference ? !root.sel.reference.locked : false
                    onClicked: App.deleteSelection()
                }
            }

            // ---- Topology ------------------------------------------------------------
            Section {
                title: tr("Topology")
                visible: !!root.sel.garment && root.sel.kind !== "reference"
                SliderRow {
                    label: tr("Particle distance")
                    from: root.imperial ? 0.2 : 0.5; to: root.imperial ? 2.4 : 6
                    stepSize: root.imperial ? 0.05 : 0.1; decimals: root.imperial ? 2 : 1; suffix: " " + root.lengthName
                    value: (root.sel.garment ? root.sel.garment.particleDistance : 2) / root.cmUnit
                    onEdited: (v) => App.setParticleDistance(v * root.cmUnit)
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: tr("Faces"); Layout.preferredWidth: 112; font.pixelSize: 12; color: palette.windowText; opacity: 0.82 }
                    ComboBox {
                        Layout.fillWidth: true
                        implicitHeight: 26
                        model: [tr("Triangles"), tr("Quads")]
                        currentIndex: root.sel.garment && root.sel.garment.topology === "Quads" ? 1 : 0
                        onActivated: App.setTopology(currentIndex === 1 ? "Quads" : "Triangles")
                    }
                }
                InfoRow {
                    label: tr("Garment mesh")
                    value: root.sel.garment ? tr("%1 faces  ·  %2 verts").arg(root.sel.garment.faces.toLocaleString(Qt.locale(), "f", 0))
                                                .arg(root.sel.garment.vertices.toLocaleString(Qt.locale(), "f", 0)) : ""
                }
                InfoRow {
                    visible: root.sel.garment ? root.sel.garment.topology === "Quads" : false
                    label: tr("Of which")
                    value: root.sel.garment ? tr("%1 quads  ·  %2 triangles").arg(root.sel.garment.quads.toLocaleString(Qt.locale(), "f", 0))
                                                .arg((root.sel.garment.faces - root.sel.garment.quads).toLocaleString(Qt.locale(), "f", 0)) : ""
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("Smaller distance = more faces: finer wrinkles, slower simulation. Quads export to OBJ and FBX; glTF and MakeHuman always receive triangles.")
                }
                NumberRow {
                    visible: root.sel.kind === "pieces"
                    label: tr("Piece override")
                    decimals: root.imperial ? 2 : 1; step: root.imperial ? 0.05 : 0.1; from: 0; suffix: root.lengthName
                    value: (root.sel.piece ? root.sel.piece.particleDistance : 0) / root.cmUnit
                    onEdited: (v) => App.setPieceValue("particleDistance", v * root.cmUnit)
                }
                InfoRow {
                    visible: root.sel.kind === "pieces"
                    label: tr("Selected")
                    value: root.sel.piece ? tr("%1 faces").arg(root.sel.piece.faces.toLocaleString(Qt.locale(), "f", 0)) + "  ·  "
                         + (root.sel.piece.area / (root.cmUnit * root.cmUnit)).toFixed(0) + " " + root.lengthName + "²" : ""
                }
                Label {
                    visible: root.sel.kind === "pieces"
                    Layout.fillWidth: true
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("Override 0 uses the garment's particle distance.")
                }
            }

            // ---- Garment thickness ---------------------------------------------------
            Section {
                title: tr("Garment Thickness")
                visible: !!root.sel.garment && root.sel.kind !== "reference"
                CheckBox {
                    Layout.fillWidth: true
                    text: tr("Enable Thickness? (Apply per Garment)")
                    checked: root.sel.garment ? root.sel.garment.thickness !== null : false
                    onToggled: App.setGarmentThicknessEnabled(root.sel.garment.id, checked)
                }
                SliderRow {
                    visible: root.sel.garment ? root.sel.garment.thickness !== null : false
                    label: tr("Thickness")
                    from: root.imperial ? 0.04 : 0.1; to: root.imperial ? 1.97 : 5
                    stepSize: root.imperial ? 0.01 : 0.1; decimals: root.imperial ? 2 : 1
                    suffix: " " + root.lengthName
                    value: (root.sel.garment && root.sel.garment.thickness !== null ? root.sel.garment.thickness : 3)
                           / 10 / root.cmUnit
                    onEdited: (v) => App.setGarmentThickness(root.sel.garment.id, v * root.cmUnit * 10)
                }
                Label {
                    visible: root.sel.garment ? root.sel.garment.thickness !== null : false
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("Applies to every piece in this garment and overrides the thickness set in Fabric Physics.")
                }
            }

            // ---- Layering ------------------------------------------------------------
            // Which garment is worn over which, and the room an outer garment leaves for the ones under it.
            Section {
                id: layering
                title: tr("Layering")
                visible: !!root.sel.garment && root.sel.kind !== "reference"
                readonly property int garmentLayer: root.sel.garment && root.sel.garment.layer !== undefined ? root.sel.garment.layer : 0
                readonly property var wornOver: root.sel.garment && root.sel.garment.wornOver ? root.sel.garment.wornOver : []
                RowLayout {
                    Layout.fillWidth: true
                    spacing: 6
                    HoverHandler { id: layerHover }
                    ToolTip.visible: layerHover.hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Which layer of clothing this garment is. 0 is worn next to the skin; a garment with a higher number is worn over the ones with lower numbers, and the simulation keeps it on their outside so they cannot poke through.")
                    Label { text: tr("Worn as layer"); Layout.preferredWidth: 112; elide: Text.ElideRight; font.pixelSize: 12; color: palette.windowText; opacity: 0.82 }
                    NumberField {
                        Layout.fillWidth: true
                        implicitHeight: 24
                        from: 0; to: App.maxGarmentLayer
                        decimals: 0; step: 1
                        value: layering.garmentLayer
                        onEdited: (v) => App.setGarmentLayer(root.sel.garment.id, Math.round(v))
                    }
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: layering.garmentLayer === 0 ? tr("0 = next to the skin. Give a garment worn over this one a higher number.")
                        : layering.wornOver.length > 0 ? tr("Worn over: %1").arg(layering.wornOver.join(", "))
                        : tr("No garment has a lower layer yet, so nothing is worn under this one.")
                }
                SliderRow {
                    label: tr("Room underneath")
                    from: 0; to: root.imperial ? 1.2 : 30
                    stepSize: root.imperial ? 0.02 : 0.5; decimals: root.imperial ? 2 : 1
                    suffix: root.imperial ? " in" : " mm"
                    value: (root.sel.garment && root.sel.garment.bodyOffset !== undefined ? root.sel.garment.bodyOffset : 0) / root.mmUnit
                    onEdited: (v) => App.setGarmentBodyOffset(root.sel.garment.id, v * root.mmUnit)
                    tip: tr("Room for layers underneath: how far this garment stays from the skin, on top of its own thickness, to leave space for what is worn under it. A jacket over a T-shirt needs the T-shirt's thickness plus a little air.")
                }
            }

            // ---- Seam ----------------------------------------------------------------
            Section {
                title: tr("Seam")
                visible: root.sel.kind === "seam"
                InfoRow { label: tr("From"); value: root.sel.seam ? root.sel.seam.a : "" }
                InfoRow { label: tr("To"); value: root.sel.seam ? root.sel.seam.b : "" }
                InfoRow {
                    label: tr("Lengths")
                    value: root.sel.seam ? (root.sel.seam.lengthA / root.cmUnit).toFixed(root.imperial ? 2 : 1) + " " + root.lengthName + "  ↔  "
                         + (root.sel.seam.lengthB / root.cmUnit).toFixed(root.imperial ? 2 : 1) + " " + root.lengthName : ""
                }
                RowLayout {
                    Layout.fillWidth: true
                    Button {
                        Layout.fillWidth: true
                        text: tr("Reverse Direction")
                        onClicked: App.reverseSeam()
                        ToolTip.visible: hovered
                        ToolTip.text: tr("Swap which ends of the two edges are sewn together (fixes a twisted seam)")
                    }
                    Button { Layout.fillWidth: true; text: tr("Delete Seam"); onClicked: App.deleteSelection() }
                }
            }

            // ---- Arrangement ---------------------------------------------------------
            Section {
                title: tr("3D Arrangement")
                visible: root.sel.kind === "pieces"
                NumberRow { label: tr("Position X"); suffix: root.imperial ? "in" : "m"; decimals: root.imperial ? 1 : 2; step: root.imperial ? 0.5 : 0.01; value: (root.sel.piece ? root.sel.piece.posX : 0) / root.mUnit; onEdited: (v) => App.setPieceValue("posX", v * root.mUnit) }
                NumberRow { label: tr("Position Y"); suffix: root.imperial ? "in" : "m"; decimals: root.imperial ? 1 : 2; step: root.imperial ? 0.5 : 0.01; value: (root.sel.piece ? root.sel.piece.posY : 0) / root.mUnit; onEdited: (v) => App.setPieceValue("posY", v * root.mUnit) }
                NumberRow { label: tr("Position Z"); suffix: root.imperial ? "in" : "m"; decimals: root.imperial ? 1 : 2; step: root.imperial ? 0.5 : 0.01; value: (root.sel.piece ? root.sel.piece.posZ : 0) / root.mUnit; onEdited: (v) => App.setPieceValue("posZ", v * root.mUnit) }
                NumberRow { label: tr("Rotation X"); suffix: "°"; decimals: 1; step: 5; value: root.sel.piece ? root.sel.piece.rotX : 0; onEdited: (v) => App.setPieceValue("rotX", v) }
                NumberRow { label: tr("Rotation Y"); suffix: "°"; decimals: 1; step: 5; value: root.sel.piece ? root.sel.piece.rotY : 0; onEdited: (v) => App.setPieceValue("rotY", v) }
                NumberRow { label: tr("Rotation Z"); suffix: "°"; decimals: 1; step: 5; value: root.sel.piece ? root.sel.piece.rotZ : 0; onEdited: (v) => App.setPieceValue("rotZ", v) }
                SliderRow {
                    label: tr("Wrap")
                    from: -300; to: 300; stepSize: 5; suffix: "°"
                    value: root.sel.piece ? root.sel.piece.curvature : 0
                    onEdited: (v) => App.setPieceValue("curvature", v)
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("Where the piece sits in 3D; you can also drag the arrows and rings in the 3D view. Wrap curls the flat piece around an arm or leg.")
                }
                InfoRow {
                    readonly property int flipped: root.sel.piece ? root.sel.piece.flippedNormals : 0
                    readonly property int count: root.sel.piece ? root.sel.piece.count : 0
                    label: tr("Normals")
                    value: flipped === 0 ? tr("As drawn") : flipped === count ? (count === 1 ? tr("Inverted") : tr("All inverted"))
                                                                         : tr("%1 of %2 inverted").arg(flipped).arg(count)
                }
                Button {
                    Layout.fillWidth: true
                    text: tr("Invert Normals")
                    onClicked: App.invertNormals()
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Turn the faces of the selected pieces to point the other way. Use View ▸ 3D: Normal Direction to see which way they face: blue is the front, red the back.")
                }
            }

            // ---- Fabric --------------------------------------------------------------
            Section {
                title: tr("Fabric Physics")
                visible: root.sel.kind === "pieces"
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: tr("Preset"); Layout.preferredWidth: 112; font.pixelSize: 12; color: palette.windowText; opacity: 0.82 }
                    ComboBox {
                        id: presetBox
                        Layout.fillWidth: true
                        implicitHeight: 26
                        // Preset names are stored in English and shown translated.
                        readonly property var names: root.sel.physics && root.sel.presets.indexOf(root.sel.physics.preset) < 0
                             ? root.sel.presets.concat([root.sel.physics.preset]) : root.sel.presets
                        model: names.map(n => root.tr(n))
                        currentIndex: root.sel.physics ? names.indexOf(root.sel.physics.preset) : -1
                        onActivated: (index) => { if (root.sel.presets.indexOf(names[index]) >= 0) App.setPreset(names[index]) }
                    }
                }
                SliderRow {
                    label: tr("Weight")
                    from: root.imperial ? 0.6 : 20; to: root.imperial ? 29.5 : 1000
                    stepSize: root.imperial ? 0.1 : 5; decimals: root.imperial ? 1 : 0
                    suffix: root.imperial ? " oz/yd²" : " g/m²"; valueWidth: root.imperial ? 70 : 54
                    value: (root.sel.physics ? root.sel.physics.density : 150) / root.gsmUnit
                    onEdited: (v) => App.setPieceValue("density", v * root.gsmUnit)
                }
                SliderRow { label: tr("Stretch"); value: root.sel.physics ? root.sel.physics.stretch : 0; onEdited: (v) => App.setPieceValue("stretch", v) }
                SliderRow { label: tr("Shear"); value: root.sel.physics ? root.sel.physics.shear : 0; onEdited: (v) => App.setPieceValue("shear", v) }
                SliderRow { label: tr("Bend"); value: root.sel.physics ? root.sel.physics.bend : 0; onEdited: (v) => App.setPieceValue("bend", v) }
                SliderRow { label: tr("Damping"); value: root.sel.physics ? root.sel.physics.damping : 0; onEdited: (v) => App.setPieceValue("damping", v) }
                SliderRow { label: tr("Friction"); value: root.sel.physics ? root.sel.physics.friction : 0; onEdited: (v) => App.setPieceValue("friction", v) }
                SliderRow {
                    label: tr("Thickness")
                    from: root.imperial ? 0.02 : 0.5; to: root.imperial ? 0.6 : 15
                    stepSize: root.imperial ? 0.02 : 0.5; decimals: root.imperial ? 2 : 1; suffix: root.imperial ? " in" : " mm"
                    value: (root.sel.physics ? root.sel.physics.thickness : 3) / root.mmUnit
                    onEdited: (v) => App.setPieceValue("thickness", v * root.mmUnit)
                }
                SliderRow {
                    label: tr("Elastic across")
                    from: 0.5; to: 1.5; stepSize: 0.01; decimals: 2; suffix: "×"
                    value: root.sel.physics && root.sel.physics.shrinkWeft !== undefined ? root.sel.physics.shrinkWeft : 1
                    onEdited: (v) => App.setPieceValue("shrinkWeft", v)
                    tip: tr("Elastic / shrink across: the width the piece pulls itself to when simulated, as a multiple of its drawn width. 1.00 is as drawn; 0.88 gathers a waistband or cuff by 12%; above 1 lets it grow.")
                }
                SliderRow {
                    label: tr("Elastic along")
                    from: 0.5; to: 1.5; stepSize: 0.01; decimals: 2; suffix: "×"
                    value: root.sel.physics && root.sel.physics.shrinkWarp !== undefined ? root.sel.physics.shrinkWarp : 1
                    onEdited: (v) => App.setPieceValue("shrinkWarp", v)
                    tip: tr("Elastic / shrink along: the same for the piece's height in the 2D pattern. 1.00 is as drawn.")
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("Elastic: 1.00 leaves the piece its drawn size. Below 1 it pulls together like elastic, across its width or along its height in the 2D pattern.")
                }
            }

            // ---- Material ------------------------------------------------------------
            Section {
                title: tr("Material")
                visible: root.sel.kind === "pieces" && !!root.sel.material
                RowLayout {
                    Layout.fillWidth: true
                    ComboBox {
                        Layout.fillWidth: true
                        implicitHeight: 26
                        model: root.sel.materials
                        textRole: "name"
                        valueRole: "id"
                        currentIndex: root.sel.material ? root.sel.materials.findIndex(m => m.id === root.sel.material.id) : -1
                        onActivated: App.assignMaterial(currentValue)
                    }
                    Button {
                        text: tr("New")
                        implicitHeight: 26
                        onClicked: App.newMaterial()
                        ToolTip.visible: hovered
                        ToolTip.text: tr("Give the selected pieces their own copy of this material")
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: tr("Name"); Layout.preferredWidth: 112; font.pixelSize: 12; color: palette.windowText; opacity: 0.82 }
                    TextField {
                        id: matNameField
                        Layout.fillWidth: true
                        implicitHeight: 24
                        leftPadding: 6; rightPadding: 6; topPadding: 2; bottomPadding: 2
                        verticalAlignment: Text.AlignVCenter
                        selectByMouse: true
                        text: root.sel.material ? root.sel.material.name : ""
                        background: Rectangle {
                            radius: 4
                            color: matNameField.activeFocus ? palette.base : Qt.alpha(palette.base, 0.88)
                            border.width: 1
                            border.color: matNameField.activeFocus ? palette.highlight : Qt.alpha(palette.mid, 0.95)
                        }
                        onEditingFinished: if (root.sel.material && text.trim() !== "" && text !== root.sel.material.name) App.renameMaterial(text)
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: tr("Colour"); Layout.preferredWidth: 112; font.pixelSize: 12; color: palette.windowText; opacity: 0.82 }
                    Rectangle {
                        id: colorSwatch
                        Layout.fillWidth: true
                        implicitHeight: 24
                        radius: 5
                        color: root.sel.material ? root.sel.material.color : "gray"
                        border.width: 1
                        border.color: swatchMouse.containsMouse ? palette.highlight : Qt.alpha(palette.windowText, 0.28)
                        MouseArea {
                            id: swatchMouse
                            anchors.fill: parent
                            hoverEnabled: true
                            cursorShape: Qt.PointingHandCursor
                            onClicked: root.pickColor(root.sel.material.color)
                        }
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: tr("Texture"); Layout.preferredWidth: 112; font.pixelSize: 12; color: palette.windowText; opacity: 0.82 }
                    Label {
                        Layout.fillWidth: true
                        elide: Text.ElideMiddle
                        text: root.sel.material && root.sel.material.textureName !== ""
                            ? (root.sel.material.textureMissing ? "⚠ " + tr("missing:") + " " : "") + root.sel.material.textureName : tr("None")
                        opacity: root.sel.material && root.sel.material.textureName !== "" ? 1 : 0.5
                        color: root.sel.material && root.sel.material.textureMissing ? App.themeWarningText : palette.windowText
                    }
                    Button { text: "…"; implicitWidth: 30; implicitHeight: 24; onClicked: root.pickTexture() }
                    Button {
                        text: "✕"; implicitWidth: 26; implicitHeight: 24
                        enabled: root.sel.material ? root.sel.material.textureName !== "" : false
                        onClicked: App.setMaterialTexture("")
                    }
                }
                CheckBox {
                    id: fitImage
                    text: tr("Fit image to piece")
                    visible: !!root.sel.material && root.sel.material.textureName !== ""
                    checked: !!root.sel.material && root.sel.material.repeat <= 0
                    onClicked: App.setMaterialValue("repeat", checked ? 0 : 1)
                }
                SliderRow {
                    id: repeatRow
                    readonly property bool tiled: !!root.sel.material && root.sel.material.tileSize > 0
                    visible: !fitImage.visible || !fitImage.checked || tiled
                    enabled: !tiled
                    opacity: tiled ? 0.45 : 1
                    label: tr("Texture repeat"); from: 0.25; to: 20; stepSize: 0.25; decimals: 2; suffix: "×"
                    value: root.sel.material ? root.sel.material.repeat : 1
                    onEdited: (v) => App.setMaterialValue("repeat", v)
                }
                NumberRow {
                    label: tr("Tile size"); suffix: root.lengthName
                    decimals: root.imperial ? 2 : 1; step: root.imperial ? 0.25 : 0.5; from: 0
                    value: (root.sel.material && root.sel.material.tileSize !== undefined ? root.sel.material.tileSize : 0) / root.cmUnit
                    onEdited: (v) => App.setMaterialValue("tileSize", v * root.cmUnit)
                    tip: tr("How much fabric one copy of the texture covers. With a tile size the texture repeats at that real size on every garment, whatever the size of its pattern, and Texture repeat is not used. 0 turns it off.")
                }
                Label {
                    visible: repeatRow.tiled
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: root.sel.material && root.sel.material.textureName === ""
                        ? tr("The tile size applies once the material has a texture.")
                        : tr("The texture repeats every %1 %2 of fabric; Texture repeat is not used. Set the tile size to 0 to use it again.")
                              .arg(root.sel.material ? (root.sel.material.tileSize / root.cmUnit).toFixed(root.imperial ? 2 : 1) : "").arg(root.lengthName)
                }
                SliderRow { label: tr("Roughness"); from: 0; to: 1; stepSize: 0.01; decimals: 2; value: root.sel.material ? root.sel.material.roughness : 0.8; onEdited: (v) => App.setMaterialValue("roughness", v) }
                SliderRow { label: tr("Metallic"); from: 0; to: 1; stepSize: 0.01; decimals: 2; value: root.sel.material ? root.sel.material.metallic : 0; onEdited: (v) => App.setMaterialValue("metallic", v) }
                SliderRow { label: tr("Opacity"); from: 0; to: 1; stepSize: 0.01; decimals: 2; value: root.sel.material ? root.sel.material.opacity : 1; onEdited: (v) => App.setMaterialValue("opacity", v) }
                Label {
                    visible: root.sel.material ? root.sel.material.users > root.sel.count : false
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: root.sel.material ? tr("Shared by %1 pieces — changes affect all of them.").arg(root.sel.material.users) : ""
                }
            }

            // ---- UV ------------------------------------------------------------------
            Section {
                title: tr("UV & Texel Density")
                visible: root.sel.kind === "pieces"
                InfoRow {
                    label: tr("Texel density")
                    value: root.sel.piece ? (root.sel.piece.texelDensity * root.cmUnit).toFixed(root.imperial ? 1 : 2) + " px/" + root.lengthName + "  @ " + root.sel.piece.textureResolution + " px" : ""
                }
                SliderRow { label: tr("UV scale"); from: 0.1; to: 4; stepSize: 0.05; decimals: 2; suffix: "×"; value: root.sel.piece ? root.sel.piece.uvScale : 1; onEdited: (v) => App.setPieceValue("uvScale", v) }
                SliderRow { label: tr("UV rotation"); from: -180; to: 180; stepSize: 5; suffix: "°"; value: root.sel.piece ? root.sel.piece.uvRotation : 0; onEdited: (v) => App.setPieceValue("uvRotation", v) }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.WordWrap
                    opacity: 0.55
                    font.pixelSize: 11
                    text: tr("UVs follow the 2D pattern layout. Switch the 2D view to UV to move pieces in texture space; turn on Checker in the 3D view to preview density.")
                }
            }

            // ---- Simulation ----------------------------------------------------------
            Section {
                title: tr("Simulation")
                visible: root.sel.kind !== "reference"
                open: root.sel.kind !== "pieces"
                CheckBox {
                    visible: !!root.sel.garment
                    Layout.fillWidth: true
                    text: tr("Also simulate with other garments")
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("When on, this garment is simulated too while another garment is the active one.")
                    checked: root.sel.garment ? root.sel.garment.simulateWithOthers : false
                    onToggled: App.setSimulateWithOthers(root.sel.garment.id, checked)
                }
                CheckBox {
                    visible: !!root.sel.garment
                    Layout.fillWidth: true
                    text: tr("Frozen")
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Freeze: hold this garment still and build the next layer on top of it")
                    checked: root.sel.garment ? !!root.sel.garment.frozen : false
                    onToggled: App.setFrozen(root.sel.garment.id, checked)
                }
                CheckBox {
                    visible: !!root.sel.garment
                    Layout.fillWidth: true
                    text: tr("Deactivated")
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Deactivate: leave this garment out of the simulation, so big outfits simulate faster. Other cloth passes through it.")
                    checked: root.sel.garment ? !!root.sel.garment.deactivated : false
                    onToggled: App.setDeactivated(root.sel.garment.id, checked)
                }
                SliderRow { label: tr("Draft distance"); from: 1; to: 5; stepSize: 0.5; decimals: 1; suffix: " cm"; value: App.draftParticleDistance; onEdited: (v) => App.setDraftParticleDistance(v) }
                SliderRow { label: tr("Gravity"); from: -20; to: 0; stepSize: 0.1; decimals: 1; suffix: " m/s²"; value: root.sel.simulation.gravity !== undefined ? root.sel.simulation.gravity : -9.8; onEdited: (v) => App.setSimulationValue("gravity", v) }
                SliderRow { label: tr("Substeps"); from: 4; to: 40; stepSize: 1; value: root.sel.simulation.substeps !== undefined ? root.sel.simulation.substeps : 12; onEdited: (v) => App.setSimulationValue("substeps", v) }
                SliderRow { label: tr("Speed"); from: 0.1; to: 2; stepSize: 0.1; decimals: 1; suffix: "×"; value: root.sel.simulation.timeScale !== undefined ? root.sel.simulation.timeScale : 1; onEdited: (v) => App.setSimulationValue("timeScale", v) }
                CheckBox {
                    Layout.fillWidth: true
                    text: tr("Cloth-to-cloth collision")
                    checked: !!root.sel.simulation.selfCollision
                    onToggled: App.setSimulationValue("selfCollision", checked ? 1 : 0)
                }
                CheckBox {
                    text: tr("Ground plane")
                    checked: !!root.sel.simulation.groundPlane
                    onToggled: App.setSimulationValue("groundPlane", checked ? 1 : 0)
                }
            }

            Item { Layout.fillHeight: true; implicitHeight: 12 }
            }
        }
    }
}
