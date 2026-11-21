import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Layers: garments, the groups inside them, and their pieces, as an indented list.
Pane {
    id: root
    padding: 0
    background: Rectangle { color: palette.window }

    property var rows: []
    function tr(text) { App.languageRevision; return App.tr(text) }
    property string editingId: ""
    property string menuId: ""
    property string menuKind: ""
    property var groupChoices: []

    // Rebuilding the list would throw away a name being typed, so wait until the rename is done.
    property bool refreshPending: false
    function refresh() {
        if (editingId !== "") {
            refreshPending = true
            return
        }
        refreshPending = false
        rows = JSON.parse(App.layers())
    }
    onEditingIdChanged: if (editingId === "" && refreshPending) Qt.callLater(refresh)
    Component.onCompleted: refresh()
    Connections {
        target: App
        function onLayersRevisionChanged() { Qt.callLater(root.refresh) }
    }

    // Where a dragged layer lands when dropped at height y of a row: dropping on a garment puts it at the end of the
    // garment's top level, the middle of a group puts it inside that group, and the upper or lower part of a row puts
    // it just before or after that row among its siblings, in any garment. Null when it cannot go there.
    function dropTarget(dragSource, to, y, height) {
        if (!dragSource || dragSource.modelData === undefined)
            return null
        const from = dragSource.modelData
        if (from.kind === "garment" || from.id === to.id)
            return null
        let place
        if (to.kind === "garment") {
            place = { zone: "into", garment: to.id, group: "", before: "" }
        } else {
            const parent = (to.kind === "group" ? to.parentId : to.groupId) || ""
            const edge = to.kind === "group" ? 0.25 : 0.5
            if (y < height * edge) {
                place = { zone: "before", garment: to.garment, group: parent, before: to.id }
            } else if (y >= height * (1 - edge)) {
                // Just below an open group is the top of its contents.
                if (to.kind === "group" && to.expanded && to.hasChildren)
                    place = { zone: "after", garment: to.garment, group: to.id, before: firstChild(to) }
                else
                    place = { zone: "after", garment: to.garment, group: parent, before: nextSibling(to) }
            } else {
                place = { zone: "into", garment: to.garment, group: to.id, before: "" }
            }
        }
        if (from.kind === "group" && place.group !== "" && (place.group === from.id || isDescendant(place.group, from.id)))
            return null
        return place
    }
    function nextSibling(r) {
        for (let i = rows.findIndex(x => x.id === r.id) + 1; i > 0 && i < rows.length; i++) {
            if (rows[i].depth < r.depth)
                break
            if (rows[i].depth === r.depth)
                return rows[i].id
        }
        return ""
    }
    function firstChild(r) {
        const i = rows.findIndex(x => x.id === r.id)
        return i >= 0 && i + 1 < rows.length && rows[i + 1].depth === r.depth + 1 ? rows[i + 1].id : ""
    }
    function isDescendant(groupId, ancestorId) {
        let current = rows.find(r => r.id === groupId)
        for (let guard = 0; current && current.parentId && guard < 64; guard++) {
            if (current.parentId === ancestorId)
                return true
            current = rows.find(r => r.id === current.parentId)
        }
        return false
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
                text: tr("Layers")
                font.pixelSize: 12
                font.weight: Font.DemiBold
                elide: Text.ElideRight
            }
        }

        ListView {
            id: list
            Layout.fillWidth: true
            Layout.fillHeight: true
            topMargin: 4
            bottomMargin: 4
            clip: true
            model: root.rows
            boundsBehavior: Flickable.StopAtBounds
            ScrollBar.vertical: ScrollBar {}

            delegate: Item {
                id: row
                required property var modelData
                width: list.width
                height: 28
                property string dropZone: ""
                property real grabX: 0
                property real grabY: 0
                property int dragCount: 1  // dragging one of several selected pieces takes them all
                function startDrag(x, y) {
                    grabX = x
                    grabY = y
                    dragCount = App.layersToMoveCount(modelData.id)
                    const p = mapToItem(Overlay.overlay, 0, 0)
                    dragProxy.x = p.x
                    dragProxy.y = p.y
                }

                Rectangle {
                    anchors { fill: parent; leftMargin: 5; rightMargin: 5; topMargin: 1; bottomMargin: 1 }
                    radius: 5
                    color: row.dropZone === "into" ? Qt.alpha(palette.highlight, 0.55)
                         : modelData.selected ? Qt.alpha(palette.highlight, 0.28)
                         : (modelData.kind === "garment" && modelData.active ? Qt.alpha(palette.highlight, 0.11)
                         : (rowMouse.containsMouse ? Qt.alpha(palette.windowText, 0.05) : "transparent"))
                    border.width: modelData.selected || row.dropZone === "into" ? 1 : 0
                    border.color: Qt.alpha(palette.highlight, 0.65)
                    Rectangle {
                        visible: modelData.kind === "garment" && modelData.active
                        anchors { left: parent.left; top: parent.top; bottom: parent.bottom; topMargin: 4; bottomMargin: 4 }
                        width: 2
                        radius: 1
                        color: palette.highlight
                    }
                }

                // A copy of the row that follows the cursor during a drag; kept in the window overlay so the
                // list cannot clip it.
                Rectangle {
                    id: dragProxy
                    parent: Overlay.overlay
                    width: row.width - 10
                    height: row.height - 2
                    visible: rowMouse.drag.active || gripMouse.drag.active
                    z: 1000
                    radius: 5
                    color: palette.highlight
                    opacity: 0.92
                    border.color: palette.highlightedText
                    border.width: 1

                    Label {
                        anchors.fill: parent
                        anchors.leftMargin: 8 + row.modelData.depth * 14
                        anchors.rightMargin: 8
                        verticalAlignment: Text.AlignVCenter
                        elide: Text.ElideRight
                        text: row.dragCount > 1 ? tr("%1 and %2 more").arg(row.modelData.name).arg(row.dragCount - 1) : row.modelData.name
                        color: palette.highlightedText
                    }

                    Drag.active: rowMouse.drag.active || gripMouse.drag.active
                    Drag.source: row
                    Drag.keys: ["seamstress-layer"]
                    Drag.mimeData: ({ "application/x-seamstress-layer": row.modelData.id })
                    Drag.hotSpot.x: row.grabX
                    Drag.hotSpot.y: row.grabY
                }

                MouseArea {
                    id: rowMouse
                    anchors.fill: parent
                    hoverEnabled: true
                    acceptedButtons: Qt.LeftButton | Qt.RightButton
                    drag.target: row.modelData.kind === "garment" ? null : dragProxy
                    drag.threshold: 12
                    drag.smoothed: false
                    onPressed: (e) => {
                        if (e.button === Qt.LeftButton && row.modelData.kind !== "garment")
                            row.startDrag(e.x, e.y)
                    }
                    onClicked: (e) => {
                        if (e.button === Qt.RightButton) {
                            if (!row.modelData.selected)
                                App.selectLayer(row.modelData.id, false)
                            root.menuId = row.modelData.id
                            root.menuKind = row.modelData.kind
                            root.groupChoices = JSON.parse(App.groupChoices())
                            contextMenu.popup()
                        } else {
                            App.selectLayer(row.modelData.id, (e.modifiers & Qt.ControlModifier) !== 0 || (e.modifiers & Qt.ShiftModifier) !== 0)
                        }
                    }
                    onDoubleClicked: (e) => { if (e.button === Qt.LeftButton) root.editingId = row.modelData.id }
                    // Internal drags are cancelled when Drag.active becomes false on release, so
                    // deliver the drop explicitly while the target still contains the drag.
                    onReleased: if (dragProxy.Drag.active) dragProxy.Drag.drop()
                }

                DropArea {
                    anchors.fill: parent
                    keys: ["seamstress-layer"]
                    function track(drag) {
                        const place = root.dropTarget(drag.source, row.modelData, drag.y, row.height)
                        row.dropZone = place ? place.zone : ""
                    }
                    onEntered: (drag) => {
                        drag.accept()
                        track(drag)
                    }
                    onPositionChanged: (drag) => track(drag)
                    onExited: row.dropZone = ""
                    onDropped: (drop) => {
                        row.dropZone = ""
                        const place = root.dropTarget(drop.source, row.modelData, drop.y, row.height)
                        if (place)
                            App.moveLayer(drop.source.modelData.id, place.garment, place.group, place.before)
                    }
                }

                // Insertion line for dropping before or after this row; below an open group it is indented to show
                // the layer goes in at the top of the group.
                Rectangle {
                    visible: row.dropZone === "before" || row.dropZone === "after"
                    x: 8 + (row.modelData.depth + (row.dropZone === "after" && row.modelData.kind === "group"
                            && row.modelData.expanded && row.modelData.hasChildren ? 1 : 0)) * 14
                    width: row.width - x - 8
                    height: 2
                    radius: 1
                    y: row.dropZone === "before" ? 0 : row.height - height
                    z: 2
                    color: palette.highlight
                }

                RowLayout {
                    anchors.fill: parent
                    anchors.leftMargin: 10 + row.modelData.depth * 14
                    anchors.rightMargin: 10
                    spacing: 4

                    // Drag handle: pieces and groups can be dragged into a group or another garment, or reordered.
                    Label {
                        Layout.preferredWidth: 8
                        text: "⋮⋮"
                        font.pixelSize: 10
                        opacity: row.modelData.kind === "garment" ? 0 : (rowMouse.containsMouse ? 0.55 : 0.25)
                        MouseArea {
                            id: gripMouse
                            anchors.fill: parent
                            anchors.margins: -4
                            enabled: row.modelData.kind !== "garment"
                            preventStealing: true
                            drag.target: dragProxy
                            drag.threshold: 4
                            drag.smoothed: false
                            cursorShape: Qt.OpenHandCursor
                            onPressed: (e) => {
                                const p = mapToItem(row, e.x, e.y)
                                row.startDrag(p.x, p.y)
                            }
                            onReleased: if (dragProxy.Drag.active) dragProxy.Drag.drop()
                        }
                    }

                    // Fold arrow for garments and groups.
                    Label {
                        Layout.preferredWidth: 12
                        text: row.modelData.hasChildren ? (row.modelData.expanded ? "▾" : "▸") : ""
                        color: row.modelData.kind === "garment" && row.modelData.active ? palette.highlight : palette.placeholderText
                        MouseArea {
                            anchors.fill: parent
                            anchors.margins: -4
                            enabled: row.modelData.hasChildren
                            cursorShape: enabled ? Qt.PointingHandCursor : Qt.ArrowCursor
                            onClicked: App.setExpanded(row.modelData.id, !row.modelData.expanded)
                        }
                    }

                    Rectangle {
                        visible: row.modelData.kind === "piece"
                        Layout.preferredWidth: 10; Layout.preferredHeight: 10
                        radius: 3
                        color: row.modelData.color || "transparent"
                        border.color: Qt.alpha(palette.windowText, 0.35)
                    }
                    Label {
                        visible: row.modelData.kind !== "piece"
                        text: row.modelData.kind === "garment" ? "◆" : "▣"
                        color: row.modelData.kind === "garment" && row.modelData.active ? palette.highlight : palette.windowText
                        opacity: row.modelData.kind === "garment" && row.modelData.active ? 0.95 : 0.65
                        font.pixelSize: 10
                    }

                    Label {
                        visible: root.editingId !== row.modelData.id
                        Layout.fillWidth: true
                        elide: Text.ElideRight
                        text: row.modelData.name
                        font.pixelSize: 12
                        font.weight: row.modelData.kind === "garment" ? Font.DemiBold : Font.Normal
                        opacity: row.modelData.visible ? 0.95 : 0.42
                    }
                    TextField {
                        id: nameEdit
                        visible: root.editingId === row.modelData.id
                        Layout.fillWidth: true
                        implicitHeight: 22
                        leftPadding: 4; rightPadding: 4; topPadding: 1; bottomPadding: 1
                        verticalAlignment: Text.AlignVCenter
                        text: row.modelData.name
                        selectByMouse: true
                        background: Rectangle {
                            radius: 4
                            color: palette.base
                            border.color: palette.highlight
                            border.width: 1
                        }
                        function start() { text = row.modelData.name; forceActiveFocus(); selectAll() }
                        // Enter or clicking elsewhere keeps the new name; Escape leaves it as it was.
                        function finish(keep) {
                            if (root.editingId !== row.modelData.id)
                                return
                            const id = row.modelData.id, value = text.trim()
                            root.editingId = ""
                            if (keep && value !== "" && value !== row.modelData.name)
                                App.rename(id, value)
                        }
                        onVisibleChanged: if (visible) start()
                        onAccepted: finish(true)
                        onActiveFocusChanged: if (!activeFocus && visible) finish(true)
                        Keys.onEscapePressed: finish(false)
                    }

                    Label {
                        visible: row.modelData.kind === "garment" && root.editingId !== row.modelData.id
                        text: row.modelData.kind === "garment" ? tr("%1 faces").arg(row.modelData.faces.toLocaleString(Qt.locale(), "f", 0)) : ""
                        font.pixelSize: 10
                        color: palette.placeholderText
                    }

                    // Garments can opt in to being simulated together with whichever garment is active.
                    ToolButton {
                        id: linkBtn
                        visible: row.modelData.kind === "garment"
                        implicitWidth: 22; implicitHeight: 22
                        padding: 0
                        text: "⇄"
                        font.pixelSize: 12
                        checkable: true
                        checked: row.modelData.kind === "garment" && row.modelData.simulateWithOthers
                        opacity: checked ? 1 : (hovered ? 0.85 : 0.38)
                        background: Rectangle {
                            radius: 4
                            color: linkBtn.checked ? Qt.alpha(palette.highlight, 0.25)
                                : linkBtn.hovered ? Qt.alpha(palette.windowText, 0.1) : "transparent"
                        }
                        onClicked: App.setSimulateWithOthers(row.modelData.id, checked)
                        ToolTip.visible: hovered
                        ToolTip.delay: 400
                        ToolTip.text: checked ? tr("Simulated together with the active garment") : tr("Only simulated when it is the active garment")
                    }

                    ToolButton {
                        id: visBtn
                        implicitWidth: 22; implicitHeight: 22
                        padding: 0
                        text: row.modelData.visible ? "◉" : "○"
                        font.pixelSize: 12
                        opacity: row.modelData.visible ? 0.88 : (hovered ? 0.75 : 0.38)
                        background: Rectangle {
                            radius: 4
                            color: visBtn.hovered ? Qt.alpha(palette.windowText, 0.1) : "transparent"
                        }
                        onClicked: App.setVisible(row.modelData.id, !row.modelData.visible)
                        ToolTip.visible: hovered
                        ToolTip.delay: 400
                        ToolTip.text: row.modelData.visible ? tr("Hide") : tr("Show")
                    }
                }
            }

            Label {
                anchors.centerIn: parent
                visible: root.rows.length === 0
                color: palette.placeholderText
                font.pixelSize: 12
                horizontalAlignment: Text.AlignHCenter
                text: tr("No garments yet")
            }
        }

        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 38
            color: palette.window
            Rectangle {
                anchors { left: parent.left; right: parent.right; top: parent.top }
                height: 1
                color: palette.mid
            }
            RowLayout {
                anchors { fill: parent; leftMargin: 8; rightMargin: 8; topMargin: 6; bottomMargin: 5 }
                spacing: 6
                component FooterButton: Button {
                    id: fb
                    Layout.fillWidth: true
                    implicitHeight: 26
                    hoverEnabled: true
                    contentItem: Label {
                        text: fb.text
                        font.pixelSize: 11
                        font.weight: Font.Medium
                        color: fb.enabled ? palette.buttonText : palette.placeholderText
                        horizontalAlignment: Text.AlignHCenter
                        verticalAlignment: Text.AlignVCenter
                        elide: Text.ElideRight
                    }
                    background: Rectangle {
                        radius: 5
                        color: !fb.enabled ? Qt.alpha(palette.button, 0.45)
                            : fb.down ? palette.dark
                            : fb.hovered ? palette.midlight
                            : palette.button
                        border.width: 1
                        border.color: !fb.enabled ? Qt.alpha(palette.mid, 0.5)
                            : fb.hovered ? palette.light : palette.mid
                        Behavior on color { ColorAnimation { duration: 90 } }
                    }
                }
                FooterButton { text: tr("+ Garment"); onClicked: App.addGarment() }
                FooterButton { text: tr("+ Group"); onClicked: App.addGroup(); enabled: root.rows.length > 0 }
                FooterButton { text: tr("Delete"); onClicked: App.deleteSelection(); enabled: root.rows.some(r => r.selected) }
            }
        }
    }

    Menu {
        id: contextMenu
        MenuItem { text: tr("Rename"); onTriggered: root.editingId = root.menuId }
        MenuItem { text: tr("Duplicate"); enabled: root.menuKind === "piece"; onTriggered: App.duplicate() }
        MenuSeparator {}
        MenuItem { text: tr("Group Selected Pieces"); enabled: root.menuKind === "piece"; onTriggered: App.groupSelection() }
        MenuItem { text: tr("Ungroup"); enabled: root.menuKind === "group"; onTriggered: App.ungroup(root.menuId) }
        Menu {
            id: moveMenu
            title: tr("Move to Group")
            enabled: root.menuKind !== "garment"
            MenuItem { text: tr("(Top level)"); onTriggered: App.moveLayer(root.menuId, "", "", "") }
            Instantiator {
                model: root.groupChoices
                delegate: MenuItem {
                    required property var modelData
                    text: modelData.name
                    enabled: modelData.id !== root.menuId
                    onTriggered: App.moveLayer(root.menuId, modelData.garment, modelData.id, "")
                }
                onObjectAdded: (index, object) => moveMenu.insertItem(index + 1, object)
                onObjectRemoved: (index, object) => moveMenu.removeItem(object)
            }
        }
        MenuSeparator {}
        MenuItem { text: tr("Delete"); onTriggered: App.deleteLayer(root.menuId) }
    }
}
