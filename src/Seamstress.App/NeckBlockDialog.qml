import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Garment ▸ Add Collar… and Add Hood…: sewn round the neckline of the active garment (a fitted tee, jacket or
// hoodie), or of the outermost garment with one. Open it with show("collar") or show("hood").
AppDialog {
    id: root
    property string mode: "collar"
    readonly property bool hood: mode === "hood"
    property string garmentName: ""
    // Each collar style's own height: a stand, a gathered cowl, the roll a hood pushed down makes.
    readonly property var styleHeights: [7, 14, 24]

    title: (hood ? tr("Add Hood to “%1”") : tr("Add Collar to “%1”")).arg(garmentName)
    modal: true
    anchors.centerIn: parent
    width: 440
    standardButtons: Dialog.Ok | Dialog.Cancel
    okText: tr("Add")

    function show(which) {
        mode = which
        garmentName = App.neckHostName()
        styleBox.currentIndex = 0
        heightSlider.value = styleHeights[0]
        lengthSlider.value = 34
        widthSlider.value = 25
        behindBox.checked = false
        open()
    }
    function length(cm) {
        return App.imperial ? (cm / 2.54).toFixed(1) + " in" : Math.round(cm) + " cm"
    }

    component OptionRow: RowLayout {
        property alias label: rowLabel.text
        property alias value: rowValue.text
        default property alias control: holder.data
        Layout.fillWidth: true
        spacing: 10
        Label { id: rowLabel; Layout.preferredWidth: 120 }
        Item { id: holder; Layout.fillWidth: true; implicitHeight: 28 }
        Label { id: rowValue; Layout.preferredWidth: 70; horizontalAlignment: Text.AlignRight; opacity: 0.8 }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 8

        Label {
            Layout.fillWidth: true
            wrapMode: Text.WordWrap
            opacity: 0.7
            font.pixelSize: 11
            text: root.hood
                ? tr("Two panels sewn round the neckline and laid down the back, as a hood falls when it is not worn. It is worn over everything on so far, so add it after the outer layers.")
                : tr("A band sewn round the neckline, worn over everything on so far.")
        }
        OptionRow {
            visible: !root.hood
            label: tr("Style")
            ComboBox {
                id: styleBox
                anchors.fill: parent
                model: [tr("Stand collar"), tr("Gathered cowl"), tr("Hood pushed down")]
                onActivated: heightSlider.value = root.styleHeights[currentIndex]
            }
        }
        OptionRow {
            visible: !root.hood
            label: styleBox.currentIndex === 2 ? tr("Round the roll") : tr("Height")
            value: root.length(heightSlider.value)
            Slider { id: heightSlider; anchors.fill: parent; from: 3; to: 34; stepSize: 1 }
        }
        OptionRow {
            visible: root.hood
            label: tr("Length")
            value: root.length(lengthSlider.value)
            Slider { id: lengthSlider; anchors.fill: parent; from: 24; to: 44; stepSize: 1 }
        }
        OptionRow {
            visible: root.hood
            label: tr("Width")
            value: root.length(widthSlider.value)
            Slider { id: widthSlider; anchors.fill: parent; from: 18; to: 34; stepSize: 1 }
        }
        CheckBox {
            id: behindBox
            visible: root.hood
            text: tr("Only round the back of the neck (under a jacket collar)")
        }
    }

    onAccepted: {
        if (hood)
            App.addHood(lengthSlider.value, widthSlider.value, behindBox.checked)
        else
            App.addCollar(["Stand", "Cowl", "Hood roll"][styleBox.currentIndex], heightSlider.value)
    }
}
