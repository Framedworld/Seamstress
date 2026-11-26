import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtQuick.Dialogs

// Playback controls in the 3D view: choose an animation (built-in or from a rigged FBX/glTF file), play or pause it
// while the garments simulate on the moving avatar, drag the timeline through what was recorded, set the speed,
// export the recording and reset to the drape from before playback.
Rectangle {
    id: bar

    function tr(text) { App.languageRevision; return App.tr(text) }
    // Re-read the C# state whenever playback changes.
    readonly property int revision: App.animationRevision
    readonly property bool active: { revision; return App.animationActive }
    readonly property bool playing: { revision; return App.animationPlaying }
    readonly property real duration: { revision; return App.animationDuration }
    readonly property real time: { revision; return App.animationTime }
    readonly property real recorded: { revision; return App.animationRecorded }
    readonly property var choices: { revision; App.languageRevision; return JSON.parse(App.animationChoices()) }
    readonly property var speeds: [0.25, 0.5, 1, 2]

    implicitWidth: row.implicitWidth + 12
    implicitHeight: 34
    radius: 7
    color: Qt.alpha(palette.window, 0.86)
    border.color: playing ? Qt.alpha("#10b981", 0.45) : Qt.alpha(palette.windowText, 0.13)
    border.width: 1

    RowLayout {
        id: row
        anchors { fill: parent; leftMargin: 6; rightMargin: 6 }
        spacing: 6

        Label { text: tr("Animation"); font.pixelSize: 11; opacity: 0.75; visible: !bar.parent || bar.parent.width > 720 }
        ComboBox {
            id: picker
            objectName: "animationPicker"
            Layout.preferredWidth: 140
            Layout.minimumWidth: 90
            Layout.fillWidth: true
            implicitHeight: 26
            font.pixelSize: 11
            model: bar.choices
            textRole: "name"
            valueRole: "id"
            currentIndex: Math.max(0, bar.choices.findIndex(c => c.id === App.animationId))
            onActivated: App.animationId = currentValue
            ToolTip.visible: hovered
            ToolTip.text: tr("Built-in pose changes need no file; Load… adds the clips of a rigged FBX or glTF file.")
        }
        ToolButton {
            text: tr("Load…")
            font.pixelSize: 11
            implicitHeight: 26
            onClicked: loadDialog.open()
            ToolTip.visible: hovered
            ToolTip.text: tr("Add the animation clips of a rigged FBX or glTF/GLB file. Its body must be the avatar.")
        }
        ToolButton {
            objectName: "animationPlay"
            text: bar.playing ? "❚❚" : "▶"
            font.pixelSize: 12
            implicitHeight: 26
            implicitWidth: 30
            onClicked: App.playAnimation()
            ToolTip.visible: hovered
            ToolTip.text: bar.playing ? tr("Pause") : tr("Play: the avatar moves and the garments simulate on it")
        }
        ToolButton {
            text: "■"
            font.pixelSize: 12
            implicitHeight: 26
            implicitWidth: 30
            enabled: bar.active
            onClicked: App.stopAnimation()
            ToolTip.visible: hovered
            ToolTip.text: tr("Reset: back to the drape from before the animation played")
        }
        Slider {
            id: scrubber
            Layout.preferredWidth: 200
            Layout.minimumWidth: 70
            Layout.fillWidth: true
            from: 0
            to: Math.max(0.01, bar.duration)
            value: bar.time
            enabled: bar.active
            onMoved: App.scrubAnimation(Math.min(value, bar.recorded))
            ToolTip.visible: hovered
            ToolTip.text: tr("Drag through what has been recorded; Play carries on from there")
            // How far the recording reaches.
            Rectangle {
                anchors { left: parent.left; verticalCenter: parent.verticalCenter; leftMargin: scrubber.leftPadding }
                width: (scrubber.availableWidth) * Math.min(1, bar.recorded / Math.max(0.01, bar.duration))
                height: 2
                color: "#10b981"
                opacity: 0.7
                z: -1
            }
        }
        Label {
            text: bar.time.toFixed(1) + " / " + bar.duration.toFixed(1) + " s"
            font.pixelSize: 11
            opacity: 0.85
            Layout.minimumWidth: implicitWidth
        }
        ComboBox {
            id: speedBox
            implicitHeight: 26
            Layout.preferredWidth: 62
            font.pixelSize: 11
            model: bar.speeds.map(s => s + "×")
            currentIndex: { bar.revision; return Math.max(0, bar.speeds.indexOf(App.animationSpeed)) }
            onActivated: App.animationSpeed = bar.speeds[currentIndex]
            ToolTip.visible: hovered
            ToolTip.text: tr("Playback speed: slower motion is easier for the cloth to follow")
        }
        ToolButton {
            text: tr("Export…")
            font.pixelSize: 11
            implicitHeight: 26
            enabled: bar.recorded > 0
            onClicked: exportDialog.open()
            ToolTip.visible: hovered
            ToolTip.text: tr("Save the recorded cloth and avatar as a glTF point cache (a morph target per frame) for Blender or a game engine")
        }
    }

    FileDialog {
        id: loadDialog
        title: tr("Load Animation")
        nameFilters: [tr("Rigged meshes") + " (*.fbx *.glb *.gltf)", tr("All files") + " (*)"]
        onAccepted: App.loadAnimationFile(selectedFile)
    }
    FileDialog {
        id: exportDialog
        title: tr("Export Recording")
        fileMode: FileDialog.SaveFile
        nameFilters: [tr("glTF point cache") + " (*.glb)"]
        onAccepted: App.exportAnimationRecording(selectedFile)
    }
}
