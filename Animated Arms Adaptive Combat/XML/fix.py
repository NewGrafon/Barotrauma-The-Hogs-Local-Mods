import xml.etree.ElementTree as ET
import sys
import os

def fix_meleeweapon(xml_path, output_path="fixed_items.xml"):
    tree = ET.parse(xml_path)
    root = tree.getroot()

    fixed_count = 0

    for item in root.findall(".//Item"):
        melee = item.find("MeleeWeapon")
        if melee is None:
            continue

        changed = False

        # Убираем проблемные атрибуты
        for attr in ["attachable", "reattachable"]:
            if attr in melee.attrib:
                del melee.attrib[attr]
                changed = True

        # Добавляем/меняем важные атрибуты для стабильности
        fixes = {
            "attackable": "false",
            "allowdropping": "true",
            "combatPriority": "0",
            "pickingtime": "0.1"
        }

        for key, value in fixes.items():
            if melee.get(key) != value:
                melee.set(key, value)
                changed = True

        if changed:
            fixed_count += 1

    tree.write(output_path, encoding="utf-8", xml_declaration=True)
    print(f"Готово! Исправлено MeleeWeapon у {fixed_count} предметов.")
    print(f"Файл сохранён как: {output_path}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Использование: python fix_meleeweapon.py путь_к_файлу.xml")
        sys.exit(1)

    input_file = sys.argv[1]

    if not os.path.exists(input_file):
        print(f"Файл не найден: {input_file}")
        sys.exit(1)

    fix_meleeweapon(input_file)