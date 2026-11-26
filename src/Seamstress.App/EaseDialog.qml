import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Fit Map ▸ Ease Tight Spots…: lets the active garment's pattern out where its drape is stretched across by more than
// a target (PatternEasing). Shows what would change before anything does; OK eases it as one undo step and simulates.
AppDialog {
    id: root
    property var plan: ({ pieces: [], seams: [], changedPieces: 0, garment: "" })
    readonly property var eased: plan.pieces.filter(p => p.eased)
    readonly property var creased: eased.filter(p => p.creasesCleared)
    readonly property var tightSeams: plan.seams.filter(s => s.mismatchCm >= 1)

    title: tr("Ease Tight Spots in “%1”").arg(plan.garment)
    modal: true
    anchors.centerIn: Overlay.overlay
    width: 480
    standardButtons: Dialog.Ok | Dialog.Cancel
    okText: tr("Ease and Simulate")

    function show() {
        refresh()
        open()
    }
    function refresh() {
        plan = JSON.parse(App.easePlan(targetSlider.value, strengthSlider.value / 100))
        const ok = standardButton(Dialog.Ok)
        if (ok)
            ok.enabled = plan.changedPieces > 0
    }
    function length(cm) {
        return App.imperial ? (cm / 2.54).toFixed(2) + " in" : cm.toFixed(1) + " cm"
    }
    onAccepted: App.applyEase(targetSlider.value, strengthSlider.value / 100)

    component OptionRow: RowLayout {
        property alias label: rowLabel.text
        property alias value: rowValue.text
        default property alias control: holder.data
        Layout.fillWidth: true
        spacing: 10
        Label { id: rowLabel; Layout.preferredWidth: 130 }
        Item { id: holder; Layout.fillWidth: true; implicitHeight: 28 }
        Label { id: rowValue; Layout.preferredWidth: 50; horizontalAlignment: Text.AlignRight; opacity: 0.8 }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 8

        Label {
            Layout.fillWidth: true
            wrapMode: Text.WordWrap
            opacity: 0.7
            font.pixelSize: 11
            text: tr("Widens each piece, row by row, only where the cloth is stretched across by more than the target, and keeps it draped where it is. Simulate again to see the result, and repeat while the stretch map still shows red. Elastic and fully pinned pieces are left alone.")
        }
        OptionRow {
            label: tr("Stretch that is fine")
            value: Math.round(targetSlider.value) + "%"
            Slider {
                id: targetSlider
                anchors.fill: parent
                from: 0; to: 10; stepSize: 1; value: 2
                onMoved: root.refresh()
            }
        }
        OptionRow {
            label: tr("Correction per pass")
            value: Math.round(strengthSlider.value) + "%"
            Slider {
                id: strengthSlider
                anchors.fill: parent
                from: 20; to: 100; stepSize: 5; value: 60
                onMoved: root.refresh()
            }
        }
        Label {
            Layout.fillWidth: true
            font.weight: Font.DemiBold
            text: root.eased.length === 0 ? tr("Nothing is stretched past the target.") : tr("Pieces to let out: %1").arg(root.eased.length)
        }
        ListView {
            Layout.fillWidth: true
            Layout.preferredHeight: Math.min(contentHeight, 180)
            clip: true
            model: root.eased
            delegate: Label {
                required property var modelData
                width: ListView.view.width
                elide: Text.ElideRight
                font.pixelSize: 11
                text: tr("%1: up to %2 wider, stretched %3%").arg(modelData.piece).arg(root.length(modelData.mostAddedCm))
                    .arg(modelData.maxStretchAcrossPercent)
            }
        }
        Label {
            visible: root.creased.length > 0
            Layout.fillWidth: true
            wrapMode: Text.WordWrap
            font.pixelSize: 11
            opacity: 0.8
            text: tr("Set creases on %1 of these pieces will be cleared: they hold the cloth stretched. Set them again once it has relaxed.").arg(root.creased.length)
        }
        Label {
            visible: root.tightSeams.length > 0
            Layout.fillWidth: true
            wrapMode: Text.WordWrap
            font.pixelSize: 11
            opacity: 0.8
            text: tr("%1 seams will join edges that differ by a centimetre or more; the longer side is eased in when sewn.").arg(root.tightSeams.length)
        }
    }
}
