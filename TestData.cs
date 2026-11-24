using System;
using System.Collections.Generic;
using System.IO;

namespace FastNote
{
    /// <summary>
    /// Sample items for --uitest runs on an empty folder, so the UI self-test and
    /// README screenshots need nothing prepared by hand. Never used on real data.
    /// </summary>
    public static class TestData
    {
        public static void Seed(string directory)
        {
            var store = new Store(directory);
            var now = DateTime.Now;
            var used = new HashSet<string>();
            string At(TimeSpan ago)
            {
                var time = now - ago;
                var stamp = time.ToString("yyyyMMddHHmmss");
                while (!used.Add(stamp))
                {
                    time = time.AddSeconds(-1);
                    stamp = time.ToString("yyyyMMddHHmmss");
                }
                return stamp;
            }
            Idea Item(double hoursAgo, string text, string tag = "", bool done = false)
            {
                return new Idea { Time = At(TimeSpan.FromHours(hoursAgo)), Text = text, Tag = tag, Done = done };
            }

            var items = new List<Idea>
            {
                Item(0.2, "周五前把季度报告初稿发给王经理", "工作"),
                Item(1, "买猫粮和洗衣液", "生活"),
                Item(3, "这次发版为什么拖了两天？", "工作"),
                Item(5, "回复设计组关于图标尺寸的邮件", "工作", true),
                Item(20, "读完《置身事内》第三章", "学习"),
                Item(26, "给房东发这个月的水电读数", "生活"),
                Item(30, "提交报销单", "工作", true),
                Item(50, "整理 Git 常用命令备忘", "学习"),
                Item(54, "Python 里 list 和 tuple 什么时候用哪个？", "学习", true),
                Item(75, "体检报告里维生素 D 偏低，要不要补？", "生活"),
                Item(98, "换掉卧室的灯泡", "生活", true),
                Item(122, "研究 WPF 虚拟化列表怎么配合动画", "学习"),
                Item(146, "看完 CS50 第 5 讲", "学习", true),
                Item(196, "约小李周末打羽毛球", "生活"),
                Item(220, "整理桌面文件", "", true),
                Item(290, "试试用番茄钟安排下午的时间"),
                Item(360, "续费云服务器", "工作", true),
                Item(480, "把旧照片备份到移动硬盘", "生活")
            };
            items[0].Starred = true;
            items[16].Starred = true;
            items[13].Shelved = true;
            items[4].Label = "读书";
            items[7].Label = "工具";
            items[12].Label = "课程";
            foreach (var idea in items)
                idea.IsQuestion = Idea.LooksLikeQuestion(idea.Text);
            items[8].Answer = "要改就用 list；内容固定、要当字典的键就用 tuple。";
            items[8].AnsweredTime = items[8].Time;

            Directory.CreateDirectory(directory);
            store.Ideas.AddRange(items);
            store.SaveTags(new[] { "工作", "生活", "学习" });
            store.Save();
            Store.Flush();
        }
    }
}
