import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import QtQuick.Layouts

// The assistant: the user asks for a change in their own words and Claude makes it on the open project with the
// agent tools, live in the views. Each tool call is one undo step ("Agent: …"). Photos or sketches can be dropped on
// the panel or attached, and the 3D view as it is can go with a message.
Pane {
    id: root
    padding: 0
    background: Rectangle { color: palette.window }

    // The 3D view, for a picture of it to go with a message.
    property Item viewport: null
    property var items: []
    property var attachments: []
    readonly property bool needsKey: !App.assistantReady
    // A picture being made smaller before it is attached (see the resizer below), and the ones waiting their turn.
    property var resizeQueue: []

    signal openPreferences()
    signal closeRequested()

    function tr(text) { App.languageRevision; return App.tr(text) }

    function refresh() {
        const atEnd = chat.atYEnd || chat.contentHeight <= chat.height
        items = JSON.parse(App.assistantItemsJson())
        attachments = JSON.parse(App.assistantAttachmentsJson())
        if (atEnd)
            Qt.callLater(() => chat.positionViewAtEnd())
    }
    Component.onCompleted: refresh()
    Connections {
        target: App
        function onAssistantRevisionChanged() { Qt.callLater(root.refresh) }
    }

    function send() {
        const text = input.text
        if (App.assistantBusy || (text.trim() === "" && attachments.length === 0))
            return
        input.text = ""
        if (includeView.checked && viewport) {
            const path = App.assistantPicturePath("view")
            const asked = viewport.saveImage(path, 1, false, function(saved) { App.assistantSend(text, saved ? path : "") })
            if (!asked)
                App.assistantSend(text, "")
        } else {
            App.assistantSend(text, "")
        }
    }

    // Pictures are made at most 1568 pixels on their longer side before they are attached: what Claude looks at
    // anyway, and a phone photo stays well under the size a request may have.
    function attach(url) {
        const s = String(url)
        if (!/\.(png|jpe?g|gif|webp|bmp)$/i.test(s)) {
            App.log("Assistant: not a picture: " + s)
            return
        }
        resizeQueue = resizeQueue.concat([s])
        if (resizer.source.toString() === "")
            nextResize()
    }
    function nextResize() {
        if (resizeQueue.length === 0) {
            resizer.source = ""
            return
        }
        const next = resizeQueue[0]
        resizeQueue = resizeQueue.slice(1)
        resizer.source = next
    }
    Item {
        width: 0; height: 0
        clip: true
        Image {
            id: resizer
            asynchronous: true
            cache: false
            sourceSize: Qt.size(1568, 1568)
            width: Math.max(1, implicitWidth)
            height: Math.max(1, implicitHeight)
            onStatusChanged: {
                if (status === Image.Ready) {
                    const from = source.toString()
                    const path = App.assistantPicturePath("picture")
                    const grabbing = grabToImage(function(result) {
                        if (!(result.saveToFile(path) && App.assistantAttach(path)))
                            App.assistantAttach(from) // could not be made smaller: send it as it is
                        root.nextResize()
                    }, Qt.size(implicitWidth, implicitHeight))
                    if (!grabbing) {
                        App.assistantAttach(from)
                        root.nextResize()
                    }
                } else if (status === Image.Error) {
                    root.nextResize()
                }
            }
        }
    }

    FileDialog {
        id: pictureDialog
        title: tr("Attach a Picture")
        fileMode: FileDialog.OpenFiles
        nameFilters: [tr("Pictures (*.png *.jpg *.jpeg *.gif *.webp *.bmp)")]
        onAccepted: {
            for (const f of selectedFiles)
                root.attach(f)
        }
    }

    DropArea {
        anchors.fill: parent
        keys: ["text/uri-list"]
        onDropped: (drop) => {
            for (const u of drop.urls) root.attach(u)
            drop.accept()
        }
        Rectangle {
            anchors.fill: parent
            visible: parent.containsDrag
            color: Qt.alpha(palette.highlight, 0.12)
            border.color: palette.highlight
            border.width: 2
            z: 10
        }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 0

        // ------------------------------------------------------------ header
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 34
            color: palette.window
            Rectangle {
                anchors { left: parent.left; right: parent.right; bottom: parent.bottom }
                height: 1
                color: palette.mid
            }
            RowLayout {
                anchors { fill: parent; leftMargin: 12; rightMargin: 4 }
                spacing: 2
                Label {
                    Layout.fillWidth: true
                    text: tr("Assistant")
                    font.pixelSize: 12
                    font.weight: Font.DemiBold
                    elide: Text.ElideRight
                }
                ToolButton {
                    text: tr("New Chat")
                    font.pixelSize: 11
                    enabled: !App.assistantBusy && root.items.length > 0
                    onClicked: App.assistantNewChat()
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Forget this conversation (the project and its undo steps stay)")
                }
                ToolButton {
                    text: "✕"
                    font.pixelSize: 11
                    onClicked: root.closeRequested()
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Hide the assistant (View ▸ Assistant)")
                }
            }
        }

        // ------------------------------------------------------------ without a key
        ColumnLayout {
            visible: root.needsKey
            Layout.fillWidth: true
            Layout.margins: 14
            spacing: 10
            Label {
                Layout.fillWidth: true
                wrapMode: Text.WordWrap
                text: tr("The assistant makes changes for you when you describe them, such as \"make the jacket baggier\" or \"add a cargo pocket to the left leg\". It uses Claude, which needs an Anthropic API key.")
            }
            Label {
                Layout.fillWidth: true
                wrapMode: Text.WordWrap
                textFormat: Text.StyledText
                linkColor: palette.link
                onLinkActivated: (link) => Qt.openUrlExternally(link)
                text: tr("Create a key at <a href=\"https://console.anthropic.com/settings/keys\">console.anthropic.com</a>, then paste it into Preferences ▸ Assistant, or set the ANTHROPIC_API_KEY environment variable and start Seamstress again.")
            }
            Button {
                text: tr("Open Preferences…")
                onClicked: root.openPreferences()
            }
            Item { Layout.fillHeight: true }
        }

        // ------------------------------------------------------------ the conversation
        ListView {
            id: chat
            visible: !root.needsKey
            Layout.fillWidth: true
            Layout.fillHeight: true
            clip: true
            topMargin: 8
            bottomMargin: 8
            spacing: 6
            model: root.items
            boundsBehavior: Flickable.StopAtBounds
            ScrollBar.vertical: ScrollBar {}

            Label {
                visible: root.items.length === 0
                anchors { left: parent.left; right: parent.right; top: parent.top; margins: 14 }
                wrapMode: Text.WordWrap
                color: palette.placeholderText
                text: tr("Describe what you want, for example \"rough up the hems\" or \"make the jacket baggier\". Drop a photo or a sketch here to have a garment made like it. Every change is one step in Edit ▸ Undo.")
            }

            delegate: Item {
                id: entry
                required property var modelData
                width: chat.width
                height: bubble.height

                Rectangle {
                    id: bubble
                    x: entry.modelData.kind === "user" ? 36 : 8
                    width: entry.width - 44
                    height: body.implicitHeight + 12
                    radius: 6
                    color: entry.modelData.kind === "user" ? Qt.alpha(palette.highlight, 0.18)
                         : entry.modelData.kind === "error" ? Qt.alpha(App.themeDangerText, 0.12)
                         : entry.modelData.kind === "tool" ? "transparent"
                         : entry.modelData.kind === "note" ? "transparent"
                         : palette.base
                    border.width: entry.modelData.kind === "user" || entry.modelData.kind === "assistant" ? 1 : 0
                    border.color: palette.mid

                    ColumnLayout {
                        id: body
                        anchors { left: parent.left; right: parent.right; top: parent.top; margins: 6 }
                        spacing: 4

                        // A tool call: what ran, and what came of it.
                        RowLayout {
                            visible: entry.modelData.kind === "tool"
                            Layout.fillWidth: true
                            spacing: 6
                            BusyIndicator {
                                visible: entry.modelData.running
                                running: visible
                                implicitWidth: 16
                                implicitHeight: 16
                            }
                            Label {
                                visible: !entry.modelData.running
                                text: entry.modelData.failed ? "✕" : "✓"
                                color: entry.modelData.failed ? App.themeDangerText : App.themeInfoText
                                font.pixelSize: 11
                            }
                            Label {
                                Layout.fillWidth: true
                                text: entry.modelData.text + (entry.modelData.detail !== "" ? "  ·  " + entry.modelData.detail : "")
                                elide: Text.ElideRight
                                font.pixelSize: 11
                                color: entry.modelData.failed ? App.themeDangerText : palette.placeholderText
                                ToolTip.visible: toolHover.hovered && entry.modelData.detail !== ""
                                ToolTip.text: entry.modelData.detail
                                HoverHandler { id: toolHover }
                            }
                        }

                        Flow {
                            visible: entry.modelData.pictures.length > 0
                            Layout.fillWidth: true
                            spacing: 4
                            Repeater {
                                model: entry.modelData.pictures
                                Image {
                                    required property var modelData
                                    source: modelData
                                    height: 64
                                    width: 64
                                    fillMode: Image.PreserveAspectCrop
                                    sourceSize: Qt.size(128, 128)
                                    asynchronous: true
                                }
                            }
                        }

                        TextEdit {
                            visible: entry.modelData.kind !== "tool" && entry.modelData.text !== ""
                            Layout.fillWidth: true
                            readOnly: true
                            selectByMouse: true
                            wrapMode: TextEdit.Wrap
                            textFormat: entry.modelData.kind === "assistant" ? TextEdit.MarkdownText : TextEdit.PlainText
                            text: entry.modelData.text
                            color: entry.modelData.kind === "error" ? App.themeDangerText
                                 : entry.modelData.kind === "note" ? palette.placeholderText : palette.text
                            font.italic: entry.modelData.kind === "note"
                            selectionColor: palette.highlight
                            selectedTextColor: palette.highlightedText
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------ what goes with the next message
        Rectangle {
            visible: !root.needsKey
            Layout.fillWidth: true
            implicitHeight: 1
            color: palette.mid
        }
        Flow {
            visible: !root.needsKey && root.attachments.length > 0
            Layout.fillWidth: true
            Layout.margins: 6
            spacing: 6
            Repeater {
                model: root.attachments
                Item {
                    required property var modelData
                    required property int index
                    width: 56
                    height: 56
                    Image {
                        anchors.fill: parent
                        source: parent.modelData
                        fillMode: Image.PreserveAspectCrop
                        sourceSize: Qt.size(112, 112)
                        asynchronous: true
                    }
                    ToolButton {
                        anchors { right: parent.right; top: parent.top }
                        width: 20
                        height: 20
                        text: "✕"
                        font.pixelSize: 9
                        onClicked: App.assistantRemoveAttachment(parent.index)
                    }
                }
            }
        }

        ColumnLayout {
            visible: !root.needsKey
            Layout.fillWidth: true
            Layout.margins: 6
            spacing: 4

            ScrollView {
                Layout.fillWidth: true
                Layout.preferredHeight: Math.min(140, Math.max(56, input.implicitHeight))
                TextArea {
                    id: input
                    objectName: "assistantInput"
                    wrapMode: TextArea.Wrap
                    placeholderText: tr("Ask for a change…  (Enter sends, Shift+Enter for a new line)")
                    Keys.onReturnPressed: (event) => {
                        if (event.modifiers & Qt.ShiftModifier) {
                            event.accepted = false
                            return
                        }
                        root.send()
                    }
                    Keys.onEnterPressed: root.send()
                }
            }
            RowLayout {
                Layout.fillWidth: true
                spacing: 4
                ToolButton {
                    text: tr("Picture…")
                    font.pixelSize: 11
                    enabled: !App.assistantBusy
                    onClicked: pictureDialog.open()
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Attach a photo or sketch of a garment (or drop one on the panel)")
                }
                CheckBox {
                    id: includeView
                    text: tr("Send the 3D view")
                    font.pixelSize: 11
                    checked: true
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Let Claude see the 3D view as it is when you send")
                }
                Item { Layout.fillWidth: true }
                Button {
                    text: App.assistantBusy ? tr("Stop") : tr("Send")
                    highlighted: !App.assistantBusy
                    enabled: App.assistantBusy || input.text.trim() !== "" || root.attachments.length > 0
                    onClicked: App.assistantBusy ? App.assistantStop() : root.send()
                }
            }
        }
    }
}
