using System.Diagnostics;
using System.Windows.Automation;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal static class CopilotQuestionAutomation
{
    public static void Apply(
        BridgeInteraction interaction,
        JsonElement answers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (interaction.VscodeProcessId <= 0)
        {
            throw new InvalidOperationException("VS Code pencere kimliği köprü heartbeat'inde bulunamadı.");
        }

        if (answers.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Copilot soru yanıtları JSON nesnesi olmalıdır.");
        }

        JsonElement[] questions = interaction.Questions;
        if (questions.Length is 0 or > 20)
        {
            throw new InvalidDataException("Copilot soru kartı boş veya desteklenen soru sınırını aşıyor.");
        }

        using Process process = Process.GetProcessById(interaction.VscodeProcessId);
        process.Refresh();
        if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Soru kartının bulunduğu VS Code ana penceresi erişilebilir değil.");
        }

        AutomationElement window = AutomationElement.FromHandle(process.MainWindowHandle);
        IReadOnlyList<Question> parsedQuestions = ParseQuestions(questions, answers);
        foreach (Question question in parsedQuestions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AutomationElement region = WaitForQuestionRegion(window, question.Title, cancellationToken);
            switch (question.Type)
            {
                case "text":
                    SetTextAnswer(region, question);
                    Advance(region, question.IsLast);
                    break;
                case "singleSelect":
                    SelectSingleOption(region, question);
                    AutomationElement? stillCurrentRegion = FindQuestionRegion(window, question.Title);
                    if (stillCurrentRegion is not null)
                    {
                        Advance(stillCurrentRegion, question.IsLast);
                    }
                    break;
                case "multiSelect":
                    SelectMultipleOptions(region, question);
                    Advance(region, question.IsLast);
                    break;
                default:
                    throw new InvalidDataException($"Desteklenmeyen Copilot soru türü: {question.Type}");
            }
        }
    }

    private static IReadOnlyList<Question> ParseQuestions(JsonElement[] source, JsonElement answers)
    {
        HashSet<string> questionIds = new(StringComparer.Ordinal);
        List<Question> result = [];
        for (int index = 0; index < source.Length; index++)
        {
            JsonElement raw = source[index];
            string id = RequiredString(raw, "id");
            string title = RequiredString(raw, "title");
            string type = RequiredString(raw, "type");
            if (!questionIds.Add(id))
            {
                throw new InvalidDataException("Copilot soru kartında yinelenen soru kimliği var.");
            }

            bool required = !raw.TryGetProperty("required", out JsonElement requiredValue)
                || requiredValue.ValueKind != JsonValueKind.False;
            if (!answers.TryGetProperty(id, out JsonElement answer))
            {
                if (required)
                {
                    throw new InvalidDataException($"Zorunlu soru için yanıt eksik: {title}");
                }

                result.Add(new Question(id, title, type, [], null, default, index == source.Length - 1));
                continue;
            }

            List<string> selectedValues = [];
            string? textValue = null;
            if (type == "text")
            {
                if (answer.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"Metin sorusunun yanıtı metin olmalıdır: {title}");
                }

                textValue = answer.GetString();
                if (textValue is null || textValue.Length > 2000)
                {
                    throw new InvalidDataException($"Metin yanıtı boş veya çok uzun: {title}");
                }
            }
            else if (type == "singleSelect")
            {
                RejectUnsupportedFreeform(answer, title);
                string selectedValue = GetAnswerString(answer, "selectedValue", title);
                ValidateOptionValues(raw, [selectedValue], title);
                selectedValues.Add(selectedValue);
            }
            else if (type == "multiSelect")
            {
                RejectUnsupportedFreeform(answer, title);
                if (answer.ValueKind != JsonValueKind.Object
                    || !answer.TryGetProperty("selectedValues", out JsonElement values)
                    || values.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"Çoklu seçim yanıtı selectedValues dizisi içermelidir: {title}");
                }

                foreach (JsonElement value in values.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException($"Seçim değeri geçerli değil: {title}");
                    }

                    string selectedValue = value.GetString() ?? string.Empty;
                    if (selectedValues.Contains(selectedValue, StringComparer.Ordinal))
                    {
                        throw new InvalidDataException($"Aynı seçenek birden fazla kez verilmiş: {title}");
                    }

                    selectedValues.Add(selectedValue);
                }
                if (required && selectedValues.Count == 0)
                {
                    throw new InvalidDataException($"Zorunlu çoklu seçim sorusunda en az bir seçenek seçilmelidir: {title}");
                }
                ValidateOptionValues(raw, selectedValues, title);
            }
            else
            {
                throw new InvalidDataException($"Desteklenmeyen Copilot soru türü: {type}");
            }

            result.Add(new Question(id, title, type, selectedValues, textValue, raw, index == source.Length - 1));
        }

        foreach (JsonProperty answer in answers.EnumerateObject())
        {
            if (!questionIds.Contains(answer.Name))
            {
                throw new InvalidDataException($"Yanıtta bu karta ait olmayan soru kimliği var: {answer.Name}");
            }
        }

        return result;
    }

    private static void RejectUnsupportedFreeform(JsonElement answer, string title)
    {
        if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("freeformValue", out _))
        {
            throw new InvalidDataException($"Bu Copilot soru kartı için serbest metin seçeneği güvenli biçimde desteklenmiyor: {title}");
        }
    }

    private static void ValidateOptionValues(JsonElement question, IReadOnlyList<string> values, string title)
    {
        if (!question.TryGetProperty("options", out JsonElement options)
            || options.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Soru seçenekleri okunamadı: {title}");
        }

        HashSet<string> allowed = options.EnumerateArray()
            .Select(option => RequiredString(option, "value"))
            .ToHashSet(StringComparer.Ordinal);
        if (values.Any(value => !allowed.Contains(value)))
        {
            throw new InvalidDataException($"Yanıtta soru kartında bulunmayan bir seçenek var: {title}");
        }
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Copilot soru kartının {propertyName} alanı eksik veya geçersiz.");
        }

        return property.GetString()!;
    }

    private static string GetAnswerString(JsonElement answer, string propertyName, string title)
    {
        if (answer.ValueKind != JsonValueKind.Object
            || !answer.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Tekli seçim yanıtında {propertyName} eksik: {title}");
        }

        return value.GetString()!;
    }

    private static AutomationElement WaitForQuestionRegion(
        AutomationElement window,
        string title,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AutomationElement? region = FindQuestionRegion(window, title);
            if (region is not null)
            {
                return region;
            }

            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static AutomationElement? FindQuestionRegion(AutomationElement window, string title)
    {
        AutomationElementCollection elements = window.FindAll(
            TreeScope.Descendants,
            Condition.TrueCondition);
        List<AutomationElement> matches = [];
        foreach (AutomationElement element in elements)
        {
            if (!IsQuestionContainer(element.Current.ControlType)
                || !element.Current.Name.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasInteractiveDescendant(element))
            {
                matches.Add(element);
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => null,
            _ => throw new InvalidOperationException($"Aynı başlığa sahip birden fazla Copilot soru kartı bulundu: {title}")
        };
    }

    private static bool IsQuestionContainer(ControlType type) =>
        type == ControlType.Group || type == ControlType.Pane || type == ControlType.Custom;

    private static bool HasInteractiveDescendant(AutomationElement region)
    {
        AutomationElementCollection descendants = region.FindAll(
            TreeScope.Descendants,
            Condition.TrueCondition);
        return descendants.Cast<AutomationElement>().Any(element =>
            element.Current.ControlType == ControlType.Button
            || element.Current.ControlType == ControlType.ListItem
            || element.Current.ControlType == ControlType.CheckBox
            || element.Current.ControlType == ControlType.Edit);
    }

    private static void SetTextAnswer(AutomationElement region, Question question)
    {
        if (question.TextValue is null)
        {
            return;
        }

        AutomationElement[] inputs = FindDescendants(region, ControlType.Edit);
        if (inputs.Length != 1 || !inputs[0].TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
        {
            throw new InvalidOperationException($"Metin alanı tekil ve erişilebilir değil: {question.Title}");
        }

        ((ValuePattern)pattern).SetValue(question.TextValue);
    }

    private static void SelectSingleOption(AutomationElement region, Question question)
    {
        if (question.SelectedValues.Count == 0)
        {
            return;
        }

        string value = question.SelectedValues[0];
        string label = FindOptionLabel(question.RawQuestion, value);
        AutomationElement option = FindUniqueDescendant(
            region,
            element => element.Current.ControlType == ControlType.ListItem
                && element.Current.Name.Contains(label, StringComparison.OrdinalIgnoreCase),
            $"Tekli seçim seçeneği bulunamadı veya belirsiz: {label}");

        if (option.TryGetCurrentPattern(InvokePattern.Pattern, out object invokePattern))
        {
            ((InvokePattern)invokePattern).Invoke();
            return;
        }

        if (option.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selectionPattern))
        {
            ((SelectionItemPattern)selectionPattern).Select();
            return;
        }

        throw new InvalidOperationException($"Copilot seçeneği tıklanabilir UI Automation deseni sunmuyor: {label}");
    }

    private static void SelectMultipleOptions(AutomationElement region, Question question)
    {
        foreach (string value in question.SelectedValues)
        {
            string label = FindOptionLabel(question.RawQuestion, value);
            AutomationElement checkbox = FindUniqueDescendant(
                region,
                element => element.Current.ControlType == ControlType.CheckBox
                    && element.Current.Name.Contains(label, StringComparison.OrdinalIgnoreCase),
                $"Çoklu seçim seçeneği bulunamadı veya belirsiz: {label}");

            if (!checkbox.TryGetCurrentPattern(TogglePattern.Pattern, out object togglePattern))
            {
                throw new InvalidOperationException($"Copilot seçeneği Toggle deseni sunmuyor: {label}");
            }

            TogglePattern toggle = (TogglePattern)togglePattern;
            if (toggle.Current.ToggleState != ToggleState.On)
            {
                toggle.Toggle();
            }
        }
    }

    private static string FindOptionLabel(JsonElement question, string value)
    {
        if (!question.TryGetProperty("options", out JsonElement options))
        {
            throw new InvalidDataException("Copilot soru seçenekleri eksik.");
        }

        JsonElement[] matches = options.EnumerateArray()
            .Where(option => option.TryGetProperty("value", out JsonElement optionValue)
                && optionValue.ValueKind == JsonValueKind.String
                && optionValue.GetString() == value)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("Copilot yanıtı tekil bir seçenekle eşleşmedi.");
        }

        return RequiredString(matches[0], "label");
    }

    private static AutomationElement FindUniqueDescendant(
        AutomationElement parent,
        Func<AutomationElement, bool> predicate,
        string error)
    {
        AutomationElementCollection descendants = parent.FindAll(
            TreeScope.Descendants,
            Condition.TrueCondition);
        AutomationElement[] matches = descendants.Cast<AutomationElement>()
            .Where(predicate)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(error);
    }

    private static AutomationElement[] FindDescendants(AutomationElement parent, ControlType controlType)
    {
        PropertyCondition condition = new(AutomationElement.ControlTypeProperty, controlType);
        AutomationElementCollection descendants = parent.FindAll(TreeScope.Descendants, condition);
        return descendants.Cast<AutomationElement>().ToArray();
    }

    private static void Advance(AutomationElement region, bool isLast)
    {
        string[] expected = isLast
            ? ["Submit", "Send answers", "Finish", "Gönder", "Yanıtları gönder", "Tamamla"]
            : ["Next", "Next question", "Continue", "Sonraki", "İleri", "Devam"];
        AutomationElement button = FindUniqueDescendant(
            region,
            element => element.Current.ControlType == ControlType.Button
                && expected.Any(name => element.Current.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                && element.Current.IsEnabled,
            isLast
                ? "Copilot soru kartının Gönder düğmesi tekil olarak belirlenemedi."
                : "Copilot soru kartının Sonraki düğmesi tekil olarak belirlenemedi.");

        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
        {
            throw new InvalidOperationException("Copilot soru kartı gezinme düğmesi tıklanabilir değil.");
        }

        ((InvokePattern)pattern).Invoke();
        Thread.Sleep(150);
    }

    private sealed record Question(
        string Id,
        string Title,
        string Type,
        IReadOnlyList<string> SelectedValues,
        string? TextValue,
        JsonElement RawQuestion,
        bool IsLast);
}
