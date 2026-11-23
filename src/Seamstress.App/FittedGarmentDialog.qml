import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Garment ▸ Add Fitted …: the garment is cut to the avatar's own measurements; these are the choices on top of that.
// Open it with show(templateName).
AppDialog {
    id: root
    property string template: ""
    readonly property bool trousers: template === "Fitted Trousers"
    readonly property bool tee: template === "Fitted Tee"
    readonly property bool hoodie: template === "Fitted Hoodie"
    readonly property bool openable: template === "Fitted Jacket" || hoodie

    title: tr("Add %1").arg(tr(template))
    modal: true
    anchors.centerIn: parent
    width: 440
    standardButtons: Dialog.Ok | Dialog.Cancel
    okText: tr("Add")

    function show(name) {
        template = name
        fitSlider.value = 0
        lengthSlider.value = 0
        sleevesBox.currentIndex = tee ? 1 : 2
        openBox.checked = false
        gapSlider.value = 8
        hoodBox.checked = true
        seamSlider.value = 0
        hemSlider.value = 0
        open()
    }

    // "4 cm", or inches with imperial units.
    function length(cm) {
        return App.imperial ? (cm / 2.54).toFixed(1) + " in" : Math.round(cm) + " cm"
    }
    function fitName(percent) {
        return percent <= -4 ? tr("Close fitting") : percent < 4 ? tr("Regular") : percent < 14 ? tr("Relaxed") : tr("Roomy")
    }

    component OptionRow: RowLayout {
        property alias label: rowLabel.text
        property alias value: rowValue.text
        default property alias control: holder.data
        Layout.fillWidth: true
        spacing: 10
        Label { id: rowLabel; Layout.preferredWidth: 120 }
        Item { id: holder; Layout.fillWidth: true; implicitHeight: 28 }
        Label { id: rowValue; Layout.preferredWidth: 96; horizontalAlignment: Text.AlignRight; opacity: 0.8 }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 8

        Label {
            Layout.fillWidth: true
            wrapMode: Text.WordWrap
            opacity: 0.7
            font.pixelSize: 11
            text: tr("Cut to the avatar's own measurements and placed round it, ready to simulate. Add garments from the skin outwards: each one is worn over what is already on.")
        }
        OptionRow {
            label: tr("Fit")
            value: root.fitName(fitSlider.value) + "  " + (fitSlider.value > 0 ? "+" : "") + Math.round(fitSlider.value) + "%"
            Slider { id: fitSlider; anchors.fill: parent; from: -8; to: 30; stepSize: 1 }
        }
        OptionRow {
            label: root.trousers ? tr("Leg length") : tr("Body length")
            value: (lengthSlider.value > 0 ? "+" : "") + root.length(lengthSlider.value)
            Slider { id: lengthSlider; anchors.fill: parent; from: -15; to: 25; stepSize: 1 }
        }
        OptionRow {
            visible: !root.trousers
            label: tr("Sleeves")
            ComboBox { id: sleevesBox; anchors.fill: parent; model: [tr("None"), tr("Short"), tr("Long")] }
        }
        OptionRow {
            visible: root.openable
            label: tr("Front")
            CheckBox { id: openBox; anchors.verticalCenter: parent.verticalCenter; text: tr("Open, unfastened") }
        }
        OptionRow {
            visible: root.openable && openBox.checked
            label: tr("Gap between fronts")
            value: root.length(gapSlider.value)
            Slider { id: gapSlider; anchors.fill: parent; from: 2; to: 30; stepSize: 1 }
        }
        OptionRow {
            visible: root.hoodie
            label: tr("Hood")
            CheckBox { id: hoodBox; anchors.verticalCenter: parent.verticalCenter; text: tr("With a hood") }
        }
        OptionRow {
            visible: root.trousers
            label: tr("Side seam back")
            value: root.length(seamSlider.value)
            Slider { id: seamSlider; anchors.fill: parent; from: 0; to: 10; stepSize: 0.5 }
            ToolTip.visible: seamHover.hovered
            ToolTip.text: tr("Moves the side seam towards the back, so pockets sewn at the front's outer edge sit on the side of the leg")
            HoverHandler { id: seamHover }
        }
        OptionRow {
            visible: root.trousers
            label: tr("Hem opening")
            value: hemSlider.value === 0 ? tr("As measured") : root.length(Math.max(hemSlider.value, 30))
            Slider { id: hemSlider; anchors.fill: parent; from: 0; to: 70; stepSize: 1 }
            ToolTip.visible: hemHover.hovered
            ToolTip.text: tr("How wide round each trouser hem. Keep it wider than the shoes, or the hem tangles on them.")
            HoverHandler { id: hemHover }
        }
    }

    onAccepted: App.addFittedTemplate(template, fitSlider.value, lengthSlider.value,
        ["none", "short", "long"][sleevesBox.currentIndex], openBox.checked, gapSlider.value, hoodBox.checked,
        seamSlider.value, hemSlider.value > 0 ? Math.max(hemSlider.value, 30) : 0)
}
