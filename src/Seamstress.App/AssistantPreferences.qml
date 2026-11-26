import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Preferences ▸ Assistant: the Claude model and the Anthropic API key. The key is never shown again once saved.
ColumnLayout {
    id: page
    spacing: 14
    function tr(text) { App.languageRevision; return App.tr(text) }

    // [{id, name}]
    readonly property var models: JSON.parse(App.assistantModelsJson())
    property bool forget: false

    function load() {
        model.currentIndex = Math.max(0, models.findIndex(m => m.id === App.settingAssistantModel))
        key.text = ""
        forget = false
    }
    function save() {
        App.saveAssistantPreferences(models[Math.max(0, model.currentIndex)].id, key.text, forget)
        key.text = ""
    }

    GroupBox {
        title: tr("Claude")
        Layout.fillWidth: true
        GridLayout {
            anchors.fill: parent
            columns: 2
            columnSpacing: 8
            rowSpacing: 8
            Label { text: tr("Model") }
            ComboBox {
                id: model
                Layout.fillWidth: true
                model: page.models.map(m => m.name)
            }
            Label { text: tr("API key") }
            RowLayout {
                Layout.fillWidth: true
                TextField {
                    id: key
                    Layout.fillWidth: true
                    echoMode: TextInput.Password
                    placeholderText: page.forget ? tr("The saved key will be removed")
                                   : App.settingAssistantKeySaved ? tr("A key is saved (type a new one to replace it)")
                                   : App.assistantKeyFromEnvironment ? tr("Using ANTHROPIC_API_KEY from the environment")
                                   : tr("Paste your Anthropic API key")
                    onTextChanged: if (text !== "") page.forget = false
                }
                Button {
                    text: tr("Remove")
                    visible: App.settingAssistantKeySaved
                    enabled: !page.forget
                    onClicked: { key.text = ""; page.forget = true }
                }
            }
            Label {
                Layout.columnSpan: 2
                Layout.fillWidth: true
                wrapMode: Text.WordWrap
                opacity: 0.6
                font.pixelSize: 11
                text: tr("Requests go to Anthropic and are billed to this key. The key is kept in your Seamstress settings on this computer only, never in projects or logs. Without one here, ANTHROPIC_API_KEY is used.")
            }
        }
    }
    Item { Layout.fillHeight: true }
}
