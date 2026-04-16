 //#pragma once
#ifndef BLOBANALYSIS_H
#define BLOBANALYSIS_H
#ifdef BLOBANALYSIS_EXPORTS
#define MAKEDLL_API extern "C" __declspec(dllexport)
#else
#define MAKEDLL_API extern "C" __declspec(dllimport)
#endif

#include "RobotBase.h"
#include "XVBase.h"
#include "XV2DBase.h"
#include <string>
namespace XVL{
	struct XVBlobPreIn
	{
		XVImage* inImage = NULL;//输入图像
		XVRegion inRoi;//输入学习区域，不能为空
		//软件界面上设置亮、暗选项，若选择亮(默认)，则灰度最小值设为140，最大值设为255；若选择暗，则灰度最小值设为0，最大值设为130；
		float inMinThre;//灰度值最小值
		float inMaxThre;//灰度值最大值
	};
	struct XVBlobPreOut
	{
		vector<XVRegion> outRegions;//输出区域数组
		float outTime;//时间
	};
	//函数名称：斑点定位预处理
	MAKEDLL_API int blobPre(XVBlobPreIn &XVBlobPre_In, XVBlobPreOut &XVBlobPre_Out);
	//返回0， 预处理成功
	//返回-1，预处理失败(输入图像为空)
	//返回-2，预处理失败(学习区域为空)
	//返回-3，预处理失败(阈值化执行失败)
	//返回-4, 预处理失败(连通性分割失败)
	//返回-5，预处理失败(抛出异常)

	struct XVBlobLearnIn
	{//斑点学习输入
		vector<XVRegion> inRegions;//输入区域数组
		vector<XVClassifyFeature> inFeatures;//学习特征信息，不能为空
		int inSelectedIndex;//选中区域的索引，默认0
	};
	struct XVBlobLearnOut
	{//斑点学习输出
		XVRegion outRegion;//学习得到的区域
		vector<XVClassifyFeature> outFeatures;//学习特征
		vector<float> outValues;//区域特征值
		XVPoint2D outCenter;//学习到的区域重心
		float outTime;//时间
	};
	//函数名称：斑点学习
	MAKEDLL_API int blobPatternLearn(XVBlobLearnIn &XVBlobLearn_In, XVBlobLearnOut &XVBlobLearn_Out);
	//返回0， 模板学习成功
	//返回-1，模板学习失败(输入区域数组为空)
	//返回-2，模板学习失败(学习特征为空)
	//返回-3，模板学习失败(抛出异常)
	//返回-4，模板区域索引越界
	
	struct XVBlobMatchIn
	{//斑点定位输入
		XVImage* inImage = NULL;//输入图像
		XVRegion inSearchRoi;//搜索区域，默认全图
		XVBlobLearnOut inModel;//Blob模板
		//软件界面上设置亮、暗选项，若选择亮(默认)，则灰度最小值设为140，最大值设为255；若选择暗，则灰度最小值设为0，最大值设为130；
		float inThreMin;//灰度最小值
		float inThreMax;//灰度最大值
		vector<float> inSimilarity;//相似度阈值，软件中设置默认值0.8
		bool inMatchMulti;//是否输出多个，软件中设置下拉菜单，共两个选项true/false，默认false
	};
	struct XVBlobMatchOut
	{//斑点定位输出
		vector<XVRegion> outRegions;//匹配得到的区域
		vector<vector<float>> outScores;//得分
		vector<XVPoint2D> outCenters;//区域中心
		vector<vector<float>> outValues;//各个区域对应的特征值
		float outTime;//时间
	};
	//函数名称:斑点定位
	MAKEDLL_API int blobPatternMatch(XVBlobMatchIn &XVBlobMatch_In, XVBlobMatchOut &XVBlobMatch_Out);
	//返回0，模板匹配成功
	//返回-1，模板匹配失败(图像为空)
	//返回-2，模板匹配失败(阈值化失败)
	//返回-3，模板匹配失败（未找到目标)
	//返回-4，模板匹配失败(抛出异常)
	//返回-5，模板匹配失败(连通性分割失败)
	//返回-6，模板匹配失败(模板为空)
	//返回-7，模板匹配失败(相似度阈值个数与学习特征个数不等)

	/*通用算法*/
	struct XVThresholdImageToRegionMonoIn
	{
		XVImage* inImage = NULL; //输入图像
		XVRegion inRegion;//常见几何图形区域(圆、矩形等)、不规则区域、ROI擦除区域，软件无需检测是否为空
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//输入局部坐标系
		float inMin;    //灰度最小值，软件中设置默认值0
		float inMax;    //灰度最大值，软件中设置默认值255
	};
	struct XVThresholdImageToRegionMonoOut
	{
		XVRegion outRegion;//阈值化区域
		float outTime;//算法运行时间,单位ms
	};
	//函数名称：灰度阈值化
	MAKEDLL_API int XVThresholdImageToRegionMono(XVThresholdImageToRegionMonoIn & XVThresholdImageToRegionMono_In, XVThresholdImageToRegionMonoOut & XVThresholdImageToRegionMono_Out);
	//返回0，算法运行成功
	//返回-1，图像输入为空



	struct XVRegionAreaIn
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionAreaOut
	{
		int outArea;//输出面积
		float outTime;//算法运行时间,单位ms 
	};
	//函数名称：区域面积
	MAKEDLL_API int XVRegionArea(XVRegionAreaIn & XVRegionArea_In, XVRegionAreaOut & XVRegionArea_Out);
	//返回0，算法运行成功



	struct XVRegionCenterIn
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionCenterOut
	{
		XVPoint2D outCenter;//输出质心
		float outTime;//算法运行时间,单位ms
	};
	//函数名称：区域重心
	MAKEDLL_API int XVRegionCenter(XVRegionCenterIn & XVRegionCenter_In, XVRegionCenterOut & XVRegionCenter_Out);
	//返回0，算法运行成功
	//返回-1，区域输入为空



	struct XVRegionRectangularityIn
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionRectangularityOut
	{
		float outRectangularity;//输出矩形度
		float outTime;//算法运行时间,单位ms
	};
	//函数名称：区域矩形度
	MAKEDLL_API int XVRegionRectangularity(XVRegionRectangularityIn & XVRegionRectangularity_In, XVRegionRectangularityOut & XVRegionRectangularity_Out);
	//返回0，算法运行成功
	//返回-1，区域输入为空



	struct XVSplitRegionToBlobsIn
	{
		XVRegion inRegion;//输入区域
		int inNeighborhood;//连通性。软件中设置下拉菜单，只有4和8两个选项，默认为8
	};
	struct XVSplitRegionToBlobsOut
	{
		vector<XVRegion> outRegions;//输出分割后的区域
		float outTime;//算法运行时间,单位ms
	};
	//函数名称：分割区域
	MAKEDLL_API int XVSplitRegionToBlobs(XVSplitRegionToBlobsIn & XVSplitRegionToBlobs_In, XVSplitRegionToBlobsOut & XVSplitRegionToBlobs_Out);
	//返回0，算法运行成功



	struct XVClassifyRegionsIn
	{
		vector<XVRegion> inRegions;//输入区域
		XVClassifyFeature inFeature;//筛选特征。软件中设置下拉菜单，有11个选项，默认面积
		float inMin;//特征最小值
		float inMax;//特征最大值
	};
	struct XVClassifyRegionsOut
	{
		vector<XVRegion> outAcceptedRegions; //满足条件的区域
		vector<XVRegion> outRejectedRegions; //不满足条件的区域
		vector<float> outAcceptedValues;    //接受区域的特征值
		vector<float> outRejectedValues;    //拒绝区域的特征值
		float outTime;//算法运行时间
	};
	//函数名称：筛选区域
	MAKEDLL_API int XVClassifyRegions(XVClassifyRegionsIn &XVClassifyRegions_In, XVClassifyRegionsOut &XVClassifyRegions_Out);
	//返回0，算法运行成功

	struct XVRegionCircularityIn
	{
		XVRegion inRegion;//输入区域
		XVCircularityMeasure inMeasure;//测量方式，软件设置下拉菜单，只有3个选项，默认值radius
	};
	struct XVRegionCircularityOut
	{
		float outCircularity;//输出似圆度
		float outTime;//运行时间,ms
	};
	//函数名称：区域圆度
	MAKEDLL_API int XVRegionCircularity(XVRegionCircularityIn & XVRegionCircularity_In, XVRegionCircularityOut & XVRegionCircularity_Out);
	//返回0，算法运行成功
	//返回-1,区域输入为空



	struct XVRegionBoundingBoxIn 
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionBoundingBoxOut 
	{
		XVBox outBox;//输出外接框
		XVPoint2D outLeftUp;//最小外接框左上角
		XVPoint2D outRightUp;//最小外接框右上角
		XVPoint2D outRightDown;//最小外接框右下角
		XVPoint2D outLeftDown;//最小外接框左下角
		XVPoint2D outCenter;//最小外接框中心点
		float outTime;//算法运行时间,单位ms
	};
	//函数名称：区域外接框
	MAKEDLL_API int XVRegionBoundingBox(XVRegionBoundingBoxIn& XVRegionBoundingBox_In, XVRegionBoundingBoxOut& XVRegionBoundingBox_Out);
	//返回0，算法运行成功
	//返回-1，区域输入为空



	struct XVRegionConvexHullIn
	{
		XVRegion inRegion;//输入区域
		bool inFlag;//操作方向标识符。软件中设置下拉菜单，只有true和false两个选项，默认为false
	};
	struct XVRegionConvexHullOut
	{
		XVPath outHull;//输出凸包
		float outHullArea;//输出凸包面积
		float outTime;//输出算法运行时间
	};
	//函数名称：区域凸包
	MAKEDLL_API int XVRegionConvexHull(XVRegionConvexHullIn & XVRegionConvexHull_In, XVRegionConvexHullOut & XVRegionConvexHull_Out);
	//返回0，算法运行成功
	//返回-1，输入区域为空



	struct XVRegionBoundingCircleIn
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionBoundingCircleOut
	{
		XVCircle2D outCircle;//输出最小外接圆
		float outTime;//输出算法运行时间
	};
	//函数名称：区域外接圆
	MAKEDLL_API int XVRegionBoundingCircle(XVRegionBoundingCircleIn & XVRegionBoundingCircle_In, XVRegionBoundingCircleOut & XVRegionBoundingCircle_Out);
	//返回0，算法运行成功
	//返回-1，输入区域为空

	struct XVRegionBoundingRectIn 
	{
		XVRegion inRegion;//输入区域
		XVRectangleOrientation inOrientation;//方向，默认水平
	};
	struct XVRegionBoundingRectOut 
	{
		XVRectangle2D outRect;//最小外接矩形
		XVPoint2D outLeftUp;//最小外接矩形左上角
		XVPoint2D outRightUp;//最小外接矩形右上角
		XVPoint2D outRightDown;//最小外接矩形右下角
		XVPoint2D outLeftDown;//最小外接矩形左下角
		XVPoint2D outCenter;//最小外接矩形中心点
		float outTime;
	};
	//函数名称：区域外接矩形(任意角度)
	MAKEDLL_API int XVRegionBoundingRect(XVRegionBoundingRectIn& XVRegionBoundingRect_In, XVRegionBoundingRectOut& XVRegionBoundingRect_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVRegionBoundingRectFixedAngleIn 
	{
		XVRegion inRegion;//输入区域
		float inAngle = 0.0f;//指定角度，默认0度
	};
	struct XVRegionBoundingRectFixedAngleOut 
	{
		XVRectangle2D outRect;//最小外接矩形
		XVPoint2D outLeftUp;//最小外接矩形左上角
		XVPoint2D outRightUp;//最小外接矩形右上角
		XVPoint2D outRightDown;//最小外接矩形右下角
		XVPoint2D outLeftDown;//最小外接矩形左下角
		XVPoint2D outCenter;//最小外接矩形中心点
		float outTime;
	};
	//函数名称：区域外接矩形(指定角度)
	MAKEDLL_API int XVRegionBoundingRectFixedAngle(XVRegionBoundingRectFixedAngleIn& XVRegionBoundingRectFixedAngle_In, XVRegionBoundingRectFixedAngleOut& XVRegionBoundingRectFixedAngle_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVRegionBoundingEllipseIn
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionBoundingEllipseOut
	{
		XVEllipse2D outEllipse;//最小外接椭圆
		float outTime;
	};
	//函数名称：区域外接椭圆
	MAKEDLL_API int XVRegionBoundingEllipse(XVRegionBoundingEllipseIn& XVRegionBoundingEllipse_In, XVRegionBoundingEllipseOut& XVRegionBoundingEllipse_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVRegionSecondOrderMomentsIn
	{
		XVRegion inRegion;//输入区域
		bool inCentral;//是否中心化,软件中设置下拉菜单，仅true/false两个选项，默认false
		bool inNormalized;//是否归一化,软件中设置下拉菜单，仅true/false两个选项，默认false
	};
	struct XVRegionSecondOrderMomentsOut
	{
		float out_11;//二阶矩_11
		float out_02;//二阶矩_02
		float out_20;//二阶矩_20
		float outTime;//输出算法运行时间
	};
	//函数名称：区域二阶矩
	MAKEDLL_API int XVRegionSecondOrderMoments(XVRegionSecondOrderMomentsIn & XVRegionSecondOrderMoments_In, XVRegionSecondOrderMomentsOut & XVRegionSecondOrderMoments_Out);                                 //计算二阶矩
	//返回0，运行成功
	//返回-1，输入区域为空



	struct XVRegionElogationIn 
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionElogationOut
	{
		float outElogation;//区域延伸率
		float outTime;//输出算法运行时间
	};
	//函数名称：区域延伸率
	MAKEDLL_API int XVRegionElogation(XVRegionElogationIn & XVRegionElogation_In, XVRegionElogationOut & XVRegionElogation_out);                                                                             //计算延伸率
	//返回0，运行成功
	//返回-1，输入区域为空

	struct XVRegionEllipticAxisIn
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionEllipticAxisOut
	{
		XVAxisInfo outMajorAxis;//区域等效椭圆长轴
		XVAxisInfo outMinorAxis;//区域等效椭圆短轴
		float outTime;//输出算法运行时间
	};
	//函数名称：区域等效椭圆
	MAKEDLL_API int XVRegionEllipticAxis(XVRegionEllipticAxisIn &XVRegionEllipticAxis_In, XVRegionEllipticAxisOut &XVRegionEllipticAxis_Out);                                 //计算椭圆的长短轴，该椭圆与给定区域有相同的一阶、二阶矩
	//返回0，运行成功
	//返回-1，输入区域为空

	struct XVRegionDiameterIn 
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionDiameterOut 
	{
		XVAxisInfo outDiameter;//直径
		float outTime;
	};
	//函数名称：区域直径
	MAKEDLL_API int XVRegionDiameter(XVRegionDiameterIn& XVRegionDiameter_In, XVRegionDiameterOut& XVRegionDiameter_Out);                                 //计算椭圆的长短轴，该椭圆与给定区域有相同的一阶、二阶矩
	//返回0，运行成功
	//返回-1，输入区域为空

	struct XVRegionOrientationIn
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionOrientationOut
	{
		float outOrientation;//区域角度，单位：度(°)
		float outTime;//输出算法运行时间
	};
	//函数名称：区域角度
	MAKEDLL_API int XVRegionOrientation(XVRegionOrientationIn & XVRegionOrientation_In, XVRegionOrientationOut & XVRegionOrientation_Out);                                                                    //计算区域方向角度
	//返回0，运行成功
	//返回-1，输入区域为空



	struct XVAlignPointIn
	{
		XVPoint2D inPoint;//输入点
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//输入局部坐标系，默认不使能(XVOptionalType::NUL)
		bool inInverse;//是否切换到逆变换，软件中设置默认值false
	};
	struct XVAlignPointOut
	{
		XVPoint2D outPoint;//输出坐标系跟随后的点
		float outTime;//输出时间
	};
	//函数名称：点变换
	MAKEDLL_API int XVAlignPoint(XVAlignPointIn &XVAlignPoint_In, XVAlignPointOut &XVAlignPoint_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败



	struct XVAlignRegionIn
	{
		XVRegion inRegion;//输入区域
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//输入局部坐标系，默认不使能(XVOptionalType::NUL)
		bool inInverse;//是否切换到逆变换，软件中设置默认值false
		int inWidth;//区域帧宽(设为图像宽度)
		int inHeight;//区域帧高（设为图像高度)
	};
	struct XVAlignRegionOut
	{
		XVRegion outRegion;//输出坐标系跟随后的区域
		float outTime;//输出时间
	};
	//函数名称：区域变换
	MAKEDLL_API int XVAlignRegion(XVAlignRegionIn &XVAlignRegion_In, XVAlignRegionOut &XVAlignRegion_Out);
	//返回0，算法运行成功

	struct XVRotateRegionIn 
	{
		XVRegion inRegion;//旋转前区域
		float inAngle;//旋转角度
		XVPoint2D inCenter;//旋转中心
		bool inInverse;//是否逆变换
		bool inPreserve;//是否保留原始尺寸
		int inWidth;//帧宽
		int inHeight;//帧高
	};
	struct XVRotateRegionOut 
	{
		XVRegion outRegion;//旋转后区域
		XVCoordinateSystem2D outAlignment;//跟随坐标系
		float outTime;
	};
	//函数名称：区域旋转
	MAKEDLL_API int XVRotateRegion(XVRotateRegionIn& XVRotateRegion_In, XVRotateRegionOut& XVRotateRegion_Out);
	//返回0，算法运行成功
	//返回-1，内存分配失败

	//函数名称：区域旋转(清边机专用)
	MAKEDLL_API int XVRotateRegionQingBianJi(XVRotateRegionIn& XVRotateRegion_In, XVRotateRegionOut& XVRotateRegion_Out);
	//返回0，算法运行成功

	struct XVRegionIntersectionIn
	{
		XVRegion inRegion1;//输入区域1，软件无需检测是否为空
		XVRegion inRegion2;//输入区域2，软件无需检测是否为空
	};
	struct XVRegionIntersectionOut
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域交集
	MAKEDLL_API int XVRegionIntersection(XVRegionIntersectionIn &XVRegionIntersection_In, XVRegionIntersectionOut &XVRegionIntersection_Out);
	//返回0，算法运行成功



	struct XVRegionUnionIn
	{
		XVRegion inRegion1;//输入区域1，软件无需检测是否为空
		XVRegion inRegion2;//输入区域2，软件无需检测是否为空
	};
	struct XVRegionUnionOut
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域并集
	MAKEDLL_API int XVRegionUnion(XVRegionUnionIn &XVRegionUnion_In, XVRegionUnionOut &XVRegionUnion_Out);
	//返回0，算法运行成功



	struct XVRegionDifferenceIn
	{
		XVRegion inRegion1;//输入区域1，软件无需检测是否为空
		XVRegion inRegion2;//输入区域2，软件无需检测是否为空
	};
	struct XVRegionDifferenceOut
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域差集
	MAKEDLL_API int XVRegionDifference(XVRegionDifferenceIn &XVRegionDifference_In, XVRegionDifferenceOut &XVRegionDifference_Out);
	//返回0，算法运行成功



	struct XVRegionComplementaryIn
	{
		XVRegion inRegion;//输入区域，软件无需检测是否为空
	};
	struct XVRegionComplementaryOut
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域补集
	MAKEDLL_API int XVRegionComplementary(XVRegionComplementaryIn &XVRegionComplementary_In, XVRegionComplementaryOut &XVRegionComplementary_Out);
	//返回0，算法运行成功

	struct XVRegionMorphIn
	{
		XVRegion inRegion;//输入区域
		XVMorphType inMorphType = XVMorphType::Dilate;//形态学运算类型，软件中设置默认值Dilate
		XVMorphShape inKernel = XVMorphShape::Rect;//核类型，软件中设置默认值Rect
		int inRadiusX = 1;//核宽一半，软件中设置默认值1，范围[0,999999]
		int inRadiusY = 1;//核高一半，软件中设置默认值1，范围[0,999999]
	};
	struct XVRegionMorphOut
	{
		XVRegion outRegion;//输出区域
		float outTime;//时间
	};
	//函数名称：区域形态变换
	MAKEDLL_API int XVRegionMorph(XVRegionMorphIn &XVRegionMorph_In, XVRegionMorphOut &XVRegionMorph_Out);
	//返回0，算法运行成功



	struct XVSortRegionIn
	{
		vector<XVRegion> inRegions;
		XVClassifyFeature inFeature;//排序特征，软件中设置默认值XVClassifyFeature::Area(面积)
		bool inAscending;//升序或降序,软件中设置默认值true(升序)
	};
	struct XVSortRegionOut
	{
		vector<XVRegion> outSortedRegions;//排序后的区域
		vector<float> outValues;//排序后区域的对应特征值
		float outTime;//时间
	};
	//函数名称：区域排序
	MAKEDLL_API int XVSortRegions(XVSortRegionIn &XVSortRegion_In, XVSortRegionOut &XVSortRegion_Out);
	//返回0，算法运行成功

	struct XVThresholdImageToRegionColorIn
	{
		XVImage* inImage = NULL;//输入图像
		XVRegion inRegion;//感兴趣区域，默认全图，绘制的区域必须能够保存，软件无需检测是否为空
		XVImageType inType;//图像类型，软件中默认RGB
		bool inDiagMode = false;//诊断模式，false(默认)关闭诊断模式，true开启诊断模式
		int inFirstMin;//第1通道最小值，软件中设置默认值0，范围0~255
		int inFirstMax;//第1通道最大值，软件中设置默认值255，范围0~255
		int inSecondMin;//第2通道最小值，软件中设置默认值0，范围0~255
		int inSecondMax;//第2通道最大值，软件中设置默认值255，范围0~255
		int inThirdMin;//第3通道最小值，软件中设置默认值0，范围0~255
		int inThirdMax;//第3通道最大值，软件中设置默认值255，范围0~255
	};
	struct XVThresholdImageToRegionColorOut
	{
		XVRegion outRegion;//输出区域
		XVImage outImage;//诊断图像，当且仅当诊断模式inDiagMode为true时，才有值且需要释放内存
		float outTime;//时间
	};
	//函数名称：彩色阈值化
	MAKEDLL_API int XVThresholdImageToRegionColor(XVThresholdImageToRegionColorIn & XVThresholdImageToRegionColor_In, XVThresholdImageToRegionColorOut & XVThresholdImageToRegionColor_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，必须输入彩色图像
	//返回-3，诊断图像内存分配失败

	struct XVRegionToImageIn 
	{
		XVRegion inRegion;
		bool isGray = true;//是否灰度图像，true表示生成灰度图像(单通道)，false表示生成彩色图像(3通道)，默认true
	};
	struct XVRegionToImageOut 
	{
		XVImage outImage;
		float outTime;
	};
	//函数名称：区域转图像
	MAKEDLL_API int XVRegionToImage(XVRegionToImageIn& XVRegionToImage_In, XVRegionToImageOut& XVRegionToImage_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败
	//返回-2，输出图像内存分配失败

	struct XVRegionHolesIn
	{
		XVRegion inRegion; //输入区域
		int inNeighborhood;//连通性。软件中设置下拉菜单，只有4和8两个选项，默认为8
		int inMinHoleArea; //孔最小面积，默认值为1
		int inMaxHoleArea;//孔最大面积，默认值为999999
	};
	struct XVRegionHolesOut
	{
		vector<XVRegion> outHoles;//输出孔洞
		float outTime;//时间
	};
	//函数名称：区域孔洞
	MAKEDLL_API int XVRegionHoles(XVRegionHolesIn &XVRegionHoles_In, XVRegionHolesOut &XVRegionHoles_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败


	struct XVFillRegionHolesIn
	{
		XVRegion inRegion; //输入区域
		int inNeighborhood;//连通性。软件中设置下拉菜单，只有4和8两个选项，默认为8
		int inMinHoleArea; //填充的孔的最小面积，默认值为1
		int inMaxHoleArea;//填充的孔的最大面积，默认值为99999999
	};
	struct XVFillRegionHolesOut
	{
		XVRegion outRegion;//填充后的区域
		float outTime;//时间
	};
	//函数名称：填充区域孔洞
	MAKEDLL_API int XVFillRegionHoles(XVFillRegionHolesIn &XVFillRegionHoles_In, XVFillRegionHolesOut &XVFillRegionHoles_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败 


	struct XVGetMaximumAndMinimumRegionIn
	{
		vector<XVRegion> inRegions;//输入区域，软件需判断是否为空，若为空，则提示警告
		XVClassifyFeature inFeature;//特征，软件设置下拉菜单，默认值:面积(XVClassifyFeature::Area)
	};
	struct XVGetMaximumAndMinimumRegionOut
	{
		int outMiniIndex;//最小区域索引
		XVRegion outMiniRegion;//最小区域
		float outMiniValue;//最小区域特征值
		int ouMaxIndex;//最大区域索引
		XVRegion outMaxRegion;//最大区域
		float outMaxValue;//最大区域特征值
		float outTime;//时间
	};
	//函数名称：获取最值区域
	MAKEDLL_API int XVGetMaximumAndMinimumRegion(XVGetMaximumAndMinimumRegionIn &XVGetMaximumAndMinimumRegion_In, XVGetMaximumAndMinimumRegionOut &XVGetMaximumAndMinimumRegion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败 
	/*通用算法*/


	struct XVRectangleRegionIn
	{
		XVRectangle2D inRectangle;//矩形，软件设置默认值(origin=(0,0),angle=0,width=200,height=120)
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//参考坐标系
		int inFrameWidth;//有效宽度，默认值1000，若在图像中绘制区域，则将其设为图像宽
		int inFrameHeight;//有效高度，默认值1000，若在图像中绘制区域，则将其设为图像高
	};
	struct XVRectangleRegionOut
	{
		XVRegion outRegion;//矩形区域
		XVRectangle2D outAlignedRectangle;//变换后矩形
		float outTime;//时间
	};
	//函数名称：创建矩形区域
	MAKEDLL_API int XVRectangleRegion(XVRectangleRegionIn &XVRectangleRegion_In, XVRectangleRegionOut &XVRectangleRegion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVCircleRegionIn
	{
		XVCircle2D inCircle;//圆形，软件设置默认值(center=(50,50),radius=50)
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//参考坐标系
		int inFrameWidth;//有效宽度，默认值1000，若在图像中绘制区域，则将其设为图像宽
		int inFrameHeight;//有效高度，默认值1000，若在图像中绘制区域，则将其设为图像高
	};
	struct XVCircleRegionOut
	{
		XVRegion outRegion;//圆形区域
		XVCircle2D outAlignedCircle;//变换后圆形
		float outTime;//时间
	};
	//函数名称：创建圆形区域
	MAKEDLL_API int XVCircleRegion(XVCircleRegionIn &XVCircleRegion_In, XVCircleRegionOut &XVCircleRegion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVRingRegionIn
	{
		XVCircleFittingField inRing;//输入区域，软件须确保inRing.axis.radius>=0且inRing.width>=0
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//参考坐标系
		BorderPosition inBorderPosition;//边界位置
		int inFrameWidth;//有效宽度，默认值1000，若在图像中绘制区域，则将其设为图像宽
		int inFrameHeight;//有效高度，默认值1000，若在图像中绘制区域，则将其设为图像高
	};
	struct XVRingRegionOut
	{
		XVRegion outRegion;
		XVCircleFittingField outAlignedRing;//变换后圆环
		float outTime;
	};
	//函数名称：创建圆环区域
	MAKEDLL_API int XVRingRegion(XVRingRegionIn& XVRingRegion_In, XVRingRegionOut& XVRingRegion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVRingSectionRegionIn 
	{
		XVCircleFittingField inRing;//输入区域，软件须确保inRing.axis.radius>0且inRing.width>0
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//参考坐标系
		float inStartAngle;//起始角度
		float inDeltaAngle;//相对角度，范围[-360,360]
		int inNum;//分段个数，默认值1，范围：>=1的整数
		int inFrameWidth;//有效宽度，默认值1000，若在图像中绘制区域，则将其设为图像宽
		int inFrameHeight;//有效高度，默认值1000，若在图像中绘制区域，则将其设为图像高
	};
	struct XVRingSectionRegionOut 
	{
		XVRegion outRegion;//输出区域
		XVCircleFittingField outAlignedRing;//变换后圆环
		float outTime;
	};
	//函数名称：创建圆环段区域
	MAKEDLL_API int XVRingSectionRegion(XVRingSectionRegionIn& XVRingSectionRegion_In, XVRingSectionRegionOut& XVRingSectionRegion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败
	//返回-2，扫描宽度太大，导致内圆半径小于0，扫描宽度必须<=2*圆半径
	//返回-3，综合角度超出360度：分段个数太多或相对角度太大

	struct XVPathRegionIn 
	{
		XVPath inPath;//多边形
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//参考坐标系
		int inFrameWidth;//有效宽度，默认值1000，若在图像中绘制区域，则将其设为图像宽
		int inFrameHeight;//有效高度，默认值1000，若在图像中绘制区域，则将其设为图像高
	};
	struct XVPathRegionOut 
	{
		XVRegion outRegion;//多边形区域
		XVPath outAlignedPath;//变换后多边形
		float outTime;//时间
	};
	//函数名称：创建多边形区域
	MAKEDLL_API int XVPathRegion(XVPathRegionIn& XVPathRegion_In, XVPathRegionOut& XVPathRegion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVSegmentRegionIn 
	{
		XVSegment2D inSegment;//线段
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//参考坐标系
		float inWidth;//线段宽度，默认值1.0，范围[1.0,1000000.0]
		int inFrameWidth;//有效宽度，默认值1000，若在图像中绘制区域，则将其设为图像宽
		int inFrameHeight;//有效高度，默认值1000，若在图像中绘制区域，则将其设为图像高
	};
	struct XVSegmentRegionOut 
	{
		XVRegion outRegion;//线区域
		XVSegment2D outAlignedSegment;//变换后线段
		float outTime;//时间
	};
	//函数名称：创建线区域
	MAKEDLL_API int XVSegmentRegion(XVSegmentRegionIn& XVSegmentRegion_In, XVSegmentRegionOut& XVSegmentRegion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVEllipseRegionIn 
	{
		XVRectangle2D inEllipse;//椭圆
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//参考坐标系
		int inFrameWidth;//有效宽度，默认值1000，若在图像中绘制区域，则将其设为图像宽
		int inFrameHeight;//有效高度，默认值1000，若在图像中绘制区域，则将其设为图像高
	};
	struct XVEllipseRegionOut 
	{
		XVRegion outRegion;//椭圆区域
		XVRectangle2D outAlignedEllipse;//变换后椭圆
		float outTime;//时间
	};
	//函数名称：创建椭圆区域
	MAKEDLL_API int XVEllipseRegion(XVEllipseRegionIn& XVEllipseRegion_In, XVEllipseRegionOut& XVEllipseRegion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVJudgePointInRegionIn
	{
		XVRegion inRegion;//输入区域，软件需判断是否为空，空不执行
		XVPoint2D inPt;//输入点，软件需判断是否为空，空不执行
	};
	struct XVJudgePointInRegionOut
	{
		bool outIsContained;//包含标志
		float outTime;//时间
	};
	//函数名称：点在区域内
	MAKEDLL_API int XVJudgePointInRegion(XVJudgePointInRegionIn &XVJudgePointInRegion_In, XVJudgePointInRegionOut &XVJudgePointInRegion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败


	struct XVPointsConvexHullIn
	{
		vector<XVPoint2D> inPoints;//输入点集
	};
	struct XVPointsConvexHullOut
	{
		XVPath outHull;//输出凸包
		float outHullArea;//输出凸包面积
		float outTime;//输出算法运行时间
	};
	//函数名称：点集凸包
	MAKEDLL_API int XVPointsConvexHull(XVPointsConvexHullIn &XVPointsConvexHull_In, XVPointsConvexHullOut &XVPointsConvexHull_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败


	struct XVRegionEqualIn
	{
		XVRegion inRegion1;//输入区域1，软件无需判断区域是否为空
		XVRegion inRegion2;//输入区域2，软件无需判断区域是否为空
	};
	struct XVRegionEqualOut
	{
		bool outIsEqual;//结果标志
		float outTime;//时间
	};
	//函数名称：区域相等
	MAKEDLL_API int XVRegionEqual(XVRegionEqualIn &XVRegionEqual_In, XVRegionEqualOut &XVRegionEqual_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败


	struct XVRegionToRegionDistanceIn
	{
		XVRegion inRegion1;//输入区域1，软件必须判断区域是否为空
		XVRegion inRegion2;//输入区域2，软件必须判断区域是否为空
	};
	struct XVRegionToRegionDistanceOut
	{
		float outDist;//区域距离
		XVSegment2D outSeg;//最短距离点对
		float outTime;//时间
	};
	//函数名称：区域距离
	MAKEDLL_API int XVRegionToRegionDistance(XVRegionToRegionDistanceIn &XVRegionToRegionDistance_In, XVRegionToRegionDistanceOut &XVRegionToRegionDistance_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败


	struct XVRegionConvexityIn
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionConvexityOut
	{
		float outConvexity;//输出凸度
		float outTime;//时间
	};
	//函数名称：区域凸度
	MAKEDLL_API int XVRegionConvexity(XVRegionConvexityIn &XVRegionConvexity_In, XVRegionConvexityOut &XVRegionConvexity_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVPointsBoundingRectangleIn
	{
		vector<XVPoint2D> inPts;
	};
	struct XVPointsBoundingRectangleOut
	{
		XVRectangle2D outRect;//最小外接矩形
		XVPoint2D outLeftUp;//最小外接矩形左上角
		XVPoint2D outRightUp;//最小外接矩形右上角
		XVPoint2D outRightDown;//最小外接矩形右下角
		XVPoint2D outLeftDown;//最小外接矩形左下角
		XVPoint2D outCenter;//最小外接矩形中心点
		float outTime;
	};
	//函数名称：点集外接矩形
	MAKEDLL_API int XVPointsBoundingRectangle(XVPointsBoundingRectangleIn &XVPointsBoundingRectangle_In, XVPointsBoundingRectangleOut &XVPointsBoundingRectangle_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVPointsBoundingCircleIn
	{
		vector<XVPoint2D>inPts;
	};
	struct XVPointsBoundingCircleOut
	{
		XVCircle2D outCircle;
		float outTime;
	};
	//函数名称：点集外接圆
	MAKEDLL_API int XVPointsBoundingCircle(XVPointsBoundingCircleIn &XVPointsBoundingCircle_In, XVPointsBoundingCircleOut &XVPointsBoundingCircle_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVPointsBoundingBoxIn
	{
		vector<XVPoint2D> inPts;
	};
	struct XVPointsBoundingBoxOut
	{
		XVBox outBox;//最小外接框
		XVPoint2D outLeftUp;//最小外接框左上角
		XVPoint2D outRightUp;//最小外接框右上角
		XVPoint2D outRightDown;//最小外接框右下角
		XVPoint2D outLeftDown;//最小外接框左下角
		XVPoint2D outCenter;//最小外接框中心点
		float outTime;
	};
	//函数名称：点集外接框
	MAKEDLL_API int XVPointsBoundingBox(XVPointsBoundingBoxIn &XVPointsBoundingBox_In, XVPointsBoundingBoxOut &XVPointsBoundingBox_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVRegionContoursIn 
	{
		XVRegion inRegion;//输入区域
		XVMode inMode;//轮廓模式，默认所有轮廓(XM_All)
		XVMethod inMethod;//轮廓显示方式，默认中心方式(XM_Center)
		int inConnectivity;//连通性，软件设置下拉菜单，包含选项4和8，默认8
	};
	struct XVRegionContoursOut 
	{
		vector<XVPath> outContours;//轮廓集合
		float outTime;//时间
	};
	//函数名称：区域轮廓
	MAKEDLL_API int XVRegionContours(XVRegionContoursIn& XVRegionContours_In, XVRegionContoursOut& XVRegionContour_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败

	struct XVGetMaximumAndMinimumRegionIntegrationIn
	{
		XVRegion inRegion;//输入区域，软件需判断是否为空，若为空，则提示警告
		int inNeighborhood;//连通性,软件中设置下拉菜单，只有4和8两个选项，默认为8
		XVClassifyFeature inFeature;//特征，软件设置下拉菜单，默认值:面积(XVClassifyFeature::Area)
	};
	struct XVGetMaximumAndMinimumRegionIntegrationOut
	{
		XVRegion outMiniRegion;//最小区域
		float outMiniValue;//最小区域特征值
		XVRegion outMaxRegion;//最大区域
		float outMaxValue;//最大区域特征值
		float outTime;//时间
	};
	//函数名称：获取最值区域(集成)
	MAKEDLL_API int XVGetMaximumAndMinimumRegionIntegration(XVGetMaximumAndMinimumRegionIntegrationIn &XVGetMaximumAndMinimumRegionIntegration_In, XVGetMaximumAndMinimumRegionIntegrationOut &XVGetMaximumAndMinimumRegionIntegration_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败 


	struct JudgeColorParam
	{
		float inFirstMin;  //第1通道最小值,默认值100.0
		float inFirstMax;  //第1通道最大值,默认值255.0
		float inSecondMin; //第2通道最小值,默认值0.0
		float inSecondMax; //第2通道最大值,默认值100.0
		float inThirdMin;  //第3通道最小值,默认值0.0
		float inThirdMax;  //第3通道最大值,默认值100.0
		string inColorType;//颜色名称，默认值"Red"
	};
	struct XVJudgeColorIn
	{
		XVImage* inImage = NULL; //输入图像
		XVRegion inRoi;   //感兴趣区域，软件无需判断是否为空
		vector<JudgeColorParam> inColorParams;//颜色参数，软件需判断是否为空，若为空，则不执行
	};
	struct XVJudgeColorOut
	{
		string outColorType;//颜色类型
		float outTime;//时间
	};
	//函数名称：颜色识别(RGB)
	MAKEDLL_API int XVJudgeColor(XVJudgeColorIn &XVJudgeColor_In, XVJudgeColorOut &XVJudgeColor_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败(图像为空)
	//返回-2，算法运行失败(图像非彩色)
	//返回-3，算法运行失败(颜色参数列表为空)

	struct ArrayRegionsUnionIn
	{
		vector<XVRegion> inRegions;
	};
	struct ArrayRegionsUnionOut
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域数组合并
	MAKEDLL_API int ArrayRegionsUnion(ArrayRegionsUnionIn &ArrayRegionsUnion_In, ArrayRegionsUnionOut &ArrayRegionsUnion_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败(区域数组为空)
	//返回-2，算法运行失败(异常)

	struct XVGetImagePixelIn
	{
		XVImage* inImage = NULL;//图像，可以是任意通道(1、2、3、4)，不能为空
		XVPoint2DInt inPoint;//位置，不能为空
	};
	struct XVGetImagePixelOut
	{
		float outValue;//平均像素值
		XVPixel outPixel;//输出像素，例如，输入图像为1通道，则ch1为有效值；输入图像为3通道，则ch1、ch2、ch3为有效值
		float outTime;
	};
	//函数名称：获取图像像素
	MAKEDLL_API int XVGetImagePixel(XVGetImagePixelIn &XVGetImagePixel_In, XVGetImagePixelOut &XVGetImagePixel_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败(图像为空)
	//返回-2，算法运行失败(采样点超出图像范围)
	//返回-3，算法运行失败(异常)

	struct XVImageAverageIn
	{
		XVImage* inImage = NULL;//图像，可以是任意通道(1、2、3、4)，不能为空
		XVRegion inRoi;//感兴趣区域，软件无需判断是否为空
	};
	struct XVImageAverageOut
	{
		float outAverageValue;//图像平均值
		XVPixel outAveragePixel;//每个通道平均值
		float outTime;
	};
	//函数名称：图像像素平均
	MAKEDLL_API int XVImageAverage(XVImageAverageIn &XVImageAverage_In, XVImageAverageOut &XVImageAverage_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，内存分配失败

	struct XVImageSumIn
	{
		XVImage* inImage = NULL;//图像，可以是任意通道(1、2、3、4)，不能为空
		XVRegion inRoi;//感兴趣区域，软件无需判断是否为空
	};
	struct XVImageSumOut
	{
		float outSumValue;//像素和均值
		XVPixel outSumPixel;//每个通道像素和
		float outTime;
	};
	//函数名称：图像像素求和
	MAKEDLL_API int XVImageSum(XVImageSumIn &XVImageSum_In, XVImageSumOut &XVImageSum_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，内存分配失败

	struct XVImageStatisticsIn 
	{
		XVImage* inImage = NULL;//输入图像，彩色或灰度图像均可
		XVRegion inRoi;//感兴趣区域，软件无需检测是否为空
	};
	struct XVImageStatisticsOut 
	{
		XVPoint2D outMinimumLocation;//最小像素值的位置
		float outMinimumValue;//最小像素值
		XVPoint2D outMaximumLocation;//最大像素值的位置
		float outMaximumValue;//最大像素值
		XVPixel outAverageColor;//各通道均值
		float outAverageValue;//总体像素均值
		XVPixel outSumColor;//各通道总和
		float outSumValue;//总体像素和
		float outStdValue;//像素标准差
		float outTime;//时间
	};
	//函数名称：像素统计
	MAKEDLL_API int XVImageStatistics(XVImageStatisticsIn& XVImageStatistics_In, XVImageStatisticsOut& XVImageStatistics_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，内存分配失败

	struct XVImageComputationIn 
	{
		XVImage* inImage1 = NULL;
		XVImage* inImage2 = NULL;
		XVRegion inRoi;
		XVImageComputationType inType = XVImageComputationType::XVC_Add;
	};
	struct XVImageComputationOut 
	{
		XVImage outImage;
		float outTime;
	};
	//函数名称：图像运算
	MAKEDLL_API int XVImageComputation(XVImageComputationIn& XVImageComputation_In, XVImageComputationOut& XVImageComputation_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输入图像类型不一致
	//返回-3，输入图像尺寸不一致
	//返回-4，算法抛出异常
	//返回-5，输出图像内存分配失败

	struct XVImageFourOperationsIn
	{
		XVImage* inImage = NULL;//输入图像
		XVImageFourOperationsType inType = XVImageFourOperationsType::XVF_ADD;//四则运算类型，默认XVImageFourOperationsType::XVF_ADD(加法)
		XVRegion inRoi;//感兴趣区域
		float inValue = 2.0f;//系数，默认2.0f，范围[-999999.0f,999999.0f];
	};
	struct XVImageFourOperationsOut
	{
		XVImage outImage;//输出图像
		float outTime;//时间
	};
	//函数名称：图像四则运算
	MAKEDLL_API int XVImageFourOperations(XVImageFourOperationsIn& XVImageFourOperations_In, XVImageFourOperationsOut& XVImageFourOperations_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输出图像内存分配失败
	//返回-3，除数为0
	
	enum ColorType
	{
		Red,//红
		Orange,//橙
		Yellow,//黄
		Green,//绿
		Cyan,//青
		Blue,//蓝
		Purple,//紫
		White,//白
		Black,//黑
		Gray//灰
	};

	struct XVCreateColorModelIn 
	{
		XVImage* inImage = NULL;//输入图像
		XVRegion inRoi;//学习区域，默认全图
		ColorType inColorName;//模板颜色名称
	};
	struct XVCreateColorModelOut 
	{
		int outHistogramHue[360];//模板色相直方图
		int outHistogramSaturation[256];//模板饱和度直方图
		int outHistogramValue[256];//模板亮度直方图
		int outMinH;//色相最小阈值
		int outMaxH;//色相最大阈值
		int outMinS;//饱和度最小阈值
		int outMaxS;//饱和度最大阈值
		int outMinV;//亮度最小阈值
		int outMaxV;//亮度最大阈值
		ColorType outColorName;//模板颜色名称
		float outTime;//时间
	};
	//函数名称：创建颜色模板
	MAKEDLL_API int XVCreateColorModel(XVCreateColorModelIn& XVCreateColorModel_In, XVCreateColorModelOut& XVCreateColorModel_Out);
	//返回0，算法运行成功
	//返回-1，图像为空
	//返回-2，模板图像必须是彩色图像

	struct XVColorIdentificationIn 
	{
		XVImage* inImage = NULL;//待识别图像
		vector<XVRegion> inRois;//感兴趣区域，默认空区域(全图)
		vector<XVCreateColorModelOut> inModels;//颜色模板
		ColorType inCurrentName;//当前颜色
		int inMinThreDelta = 0;//最小迟滞阈值，彩色阈值化最小值减去该值，范围[-255,255]
		int inMaxThreDelta = 0;//最大迟滞阈值，彩色阈值化最大值加上该值，范围[-255,255]
		int inRadiusX = 5;
		int inRadiusY = 5;
	};
	struct XVColorIdentificationOut 
	{
		vector<ColorType> outColorNames;//当前颜色名称
		vector<float> outColorScores;//当前颜色分数，表示当前图像判定为该颜色的概率
		vector<XVRegion> outColorRegions;//当前颜色区域
		vector<int>outColorAreas;//当前颜色面积
		float outTime;//时间
	};
	//函数名称：装棉机颜色识别
	MAKEDLL_API int XVColorIdentification(XVColorIdentificationIn& XVColorIdentification_In, XVColorIdentificationOut& XVColorIdentification_Out);
	//返回0，算法运行成功
	//返回-1，图像为空
	//返回-2，不是彩色图像
	//返回-3，颜色模型为空
	//返回-4，匹配区域为空

	struct XVRegionDistanceTransformIn 
	{
		XVRegion inRegion;//输入区域
		XVDistanceMeasuring inType;//距离类型，默认XVD_Euclid(欧氏距离)
		XVMaskSize inSize;//掩模尺寸，默认XVThree(边长为3)
	};

	struct XVRegionDistanceTransformOut 
	{
		float* outData;//变换结果
		int outWidth;//框架宽度
		int outHeight;//框架高度
		float outTime;//时间
	};
	//函数名称：区域距离变换
	MAKEDLL_API int XVRegionDistanceTransform(XVRegionDistanceTransformIn& XVRegionDistanceTransform_In, XVRegionDistanceTransformOut& XVRegionDistanceTransform_Out);
	//返回0，算法运行成功

	struct XVThresholdDynamicIn 
	{
		XVImage* inImage = NULL;//输入图像，彩色/灰度图像均可
		XVRegion inRoi;//输入感兴趣区域，可以为空
		int inRadiusX;//核宽一半，软件中设置默认值3，范围[0,999999]
		int inRadiusY;//核高一半，软件中设置默认值3，范围[0,999999]
		int inMinThre;//最小阈值，软件中设置默认值-5，范围[-255,255]
		int inMaxThre;//最大阈值，软件中设置默认值5，范围[-255,255]
	};
	struct XVThresholdDynamicOut 
	{
		XVRegion outRegion;//输出区域
		float outTime;//时间
	};
	//函数名称：动态阈值化
	MAKEDLL_API int XVThresholdDynamic(XVThresholdDynamicIn& XVThresholdDynamic_In, XVThresholdDynamicOut& XVThresholdDynamic_Out);
	//返回0，算法运行成功
	//返回-1，图像为空
	//返回-2，内存分配失败

	struct XVGeometricTransformationIn 
	{
		XVImage* inImage = NULL;//输入图像，灰度或彩色图像
		XVRectangle2D inRoi;//感兴趣区域
		XVTransformationType inTransformationType = XVTransformationType::Shift;//变换类型，默认平移模式(Shift)
		XVInterpolationMethod inInterpolation = XVInterpolationMethod::NearestNeighbor;//插值法，默认最近邻(NearestNeighbor)

		//仅镜像模式下开放
		XVMirrorType inMirrorType = XVMirrorType::VerticalMirror;//镜像类型，默认垂直镜像(VerticalMirror)

		//仅旋转模式下开放
		XVSizeMode inSizeMode = XVSizeMode::XVS_Fit;//尺寸模式，默认自适应(XVS_Fit)

		//仅仿射、平移模式下开放
		int inShiftX = 0;//x方向平移距离，默认0
		int inShiftY = 0;//y方向平移距离，默认0

		//仅在缩放模式下开放
		XVScaleMethod inScaleMethod = XVScaleMethod::Relative;//缩放方式，默认相对(Relative)
		//仅在缩放方式为相对时开放
		float inScaleHorizontal = 1.0f;//水平缩放比例，默认1.0，范围[0.1,10.0]
		float inScaleVertical = 1.0f;//垂直缩放比例，默认1.0，范围[0.1,10.0]
		//仅在缩放方式为绝对时开放
		int inNewWidth = 1000;//图像新宽度，默认1000，范围[1,65535]
		int inNewHeight = 1000;//图像新高度，默认1000，范围[1,65535]

		//仅仿射模式下开放
		float inScale = 1.0f;//缩放比例，默认1.0，范围[0.1,10.0]

		//仅仿射、旋转模式下开放
		float inAngle = 0.0f;//旋转角度，默认0.0
	};
	struct XVGeometricTransformationOut 
	{
		XVImage outImage;//输出图像
		float outTime;//时间
	};
	//函数名称：图像几何变换
	MAKEDLL_API int XVGeometricTransformation(XVGeometricTransformationIn& XVGeometricTransformation_In, XVGeometricTransformationOut& XVGeometricTransformation_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，内存分配失败
	//返回-3，调用加速函数失败

	struct XVImageNormalizationIn 
	{
		XVImage* inImage = NULL;//输入图像，必须是灰度图像
		XVImageNormalizationType  inType;//归一化类型，设置默认值：直方图均衡化(HistogramEqualization)
		XVRegion inRoi;//感兴趣区域

		/*仅在选择"直方图归一化"类型时显示*/
		//必须保证左右端比例之和不超过1.0，例如左端比例为0.6,右端比例为0.7，虽然满足各自的范围条件，但是比例之和为1.3超过了1.0，此时上位机应将右端比例强行修改为1-0.6=0.4
		float inLeftProportion;//左端像素忽略比例,默认0.0，范围[0.0,1.0]
		float inRightProportion;//右端像素忽略比例,默认0.0,范围[0.0,1.0]
		int inTargetMinValue;//目标最小灰度值，默认0，范围[0,255]
		int inTargetMaxValue;//目标最大灰度值，默认255，范围[0,255]
		/*仅在选择"直方图归一化"类型时显示*/

		/*仅在选择"均值标准差归一化"类型时显示*/
		float inTargetAverage;//目标均值，默认125.0，范围[0.0，255.0]
		float inTargetStandardDeviation;//目标标准差，默认20.0，范围[0.0，255.0]
		/*仅在选择"均值标准差归一化"类型时显示*/
	};
	struct XVImageNormalizationOut 
	{
		XVImage outImage;//归一化后的图像
		float outTime;//时间
	};
	//函数名称：图像归一化
	MAKEDLL_API int XVImageNormalization(XVImageNormalizationIn& XVImageNormalization_In, XVImageNormalizationOut& XVImageNormalization_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输入图像必须是灰度图像
	//返回-3，输出图像内存分配失败

	struct XVMergingImagesIn 
	{
		XVImage* inImage = NULL;//输入图像
		int inRows;//行数，默认2，范围[1,10]
		int inCols;//列数，默认2，范围[1,10]
		int inIndex;//索引，默认0，范围[0,inRows*inCols)
	};
	struct XVMergingImagesOut 
	{
		XVImage outImage;//
		float outTime;//时间
	};
	//函数名称：
	MAKEDLL_API int XVMergingImages(XVMergingImagesIn& XVMergingImages_In, XVMergingImagesOut& XVMergingImages_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输出图像内存分配失败

	struct XVRegionInnerBoxIn 
	{
		XVRegion inRegion;//输入区域
		int inMinWidth;//内接框最小宽度，默认值：1
		int inMaxWidth;//内接框最大宽度，默认值：999999
		int inMinHeight;//内接框最小高度，默认值：1
		int inMaxHeight;//内接框最大高度，默认值：999999
	};
	struct XVRegionInnerBoxOut 
	{
		XVBox outBox;//输出内接框
		XVPoint2D outLeftUp;//最小内接框左上顶点
		XVPoint2D outRightUp;//最小内接框右上顶点
		XVPoint2D outRightDown;//最小内接框右下顶点
		XVPoint2D outLeftDown;//最小内接框左下顶点
		XVPoint2D outCenter;//最小内接框中心点
		float outTime;//算法运行时间,单位ms
	};
	//函数名称：区域内接框
	MAKEDLL_API int XVRegionInnerBox(XVRegionInnerBoxIn& XVRegionInnerBox_In, XVRegionInnerBoxOut& XVRegionInnerBox_Out);
	//返回0，算法运行成功
	//返回-1，区域输入为空
	//返回-2，内存分配失败

	struct XVRegionInnerCircleIn 
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionInnerCircleOut 
	{
		XVCircle2D outCircle;//内接圆
		float outTime;//算法运行时间,单位ms
	};
	//函数名称：区域内接圆
	MAKEDLL_API int XVRegionInnerCircle(XVRegionInnerCircleIn& XVRegionInnerCircle_In, XVRegionInnerCircleOut& XVRegionInnerCircle_Out);
	//返回0，算法运行成功
	//返回-1，区域输入为空


	struct XVRegionInnerRectangleIn 
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionInnerRectangleOut 
	{
		XVRectangle2D outRect;//最大内接矩形
		XVPoint2D outLeftUp;//最大内接矩形左上顶点
		XVPoint2D outRightUp;//最大内接矩形右上顶点
		XVPoint2D outRightDown;//最大内接矩形右下顶点
		XVPoint2D outLeftDown;//最大内接矩形左下顶点
		XVPoint2D outCenter;//最大内接矩形中心点
		float outTime;//时间
	};

	//函数名称：区域内接矩形
	MAKEDLL_API int XVRegionInnerRectangle(XVRegionInnerRectangleIn& XVRegionInnerRectangle_In, XVRegionInnerRectangleOut& XVRegionInnerRectangle_Out);
	//返回0，算法运行成功
	//返回-1，区域输入为空

	struct XVResizeRegionIn 
	{
		XVRegion inRegion;
		int inNewWidth;//新区域宽度，默认1000，范围[1,10000]
		int inNewHeight;//新区域高度，默认1000，范围[1,10000]
	};
	struct XVResizeRegionOut 
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域缩放
	MAKEDLL_API int XVResizeRegion(XVResizeRegionIn& XVResizeRegion_In, XVResizeRegionOut& XVResizeRegion_Out);
	//返回0，算法运行成功
	//返回-1，区域输入为空

	struct XVCropRegionToBoxIn 
	{
		XVRegion inRegion;
		XVBox inBox;
	};
	struct XVCropRegionToBoxOut 
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域裁剪(框)
	MAKEDLL_API int XVCropRegionToBox(XVCropRegionToBoxIn& XVCropRegionToBox_In, XVCropRegionToBoxOut& XVCropRegionToBox_Out);
	//返回0，算法运行成功
	//返回-1，区域输入为空

	struct XVRegionDilateIn 
	{
		XVRegion inRegion;
		int inRadiusX = 1;//核宽一半
		int inRadiusY = 1;//核高一半
		XVMorphShape inKernel = XVMorphShape::Rect;
	};
	struct XVRegionDilateOut 
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域膨胀
	MAKEDLL_API int XVRegionDilate(XVRegionDilateIn& XVDilate_In, XVRegionDilateOut& XVDilate_Out);
	//返回0，算法运行成功
	//返回-1，内存分配失败

	struct XVRegionErodeIn 
	{
		XVRegion inRegion;
		int inRadiusX = 1;//核宽一半
		int inRadiusY = 1;//核高一半
		XVMorphShape inKernel = XVMorphShape::Rect;
	};
	struct XVRegionErodeOut 
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域腐蚀
	MAKEDLL_API int XVRegionErode(XVRegionErodeIn& XVErode_In, XVRegionErodeOut& XVErode_Out);
	//返回0，算法运行成功
	//返回-1，内存分配失败

	struct XVCompose3In 
	{
		XVImage* inImage1 = NULL;
		XVImage* inImage2 = NULL;
		XVImage* inImage3 = NULL;
	};
	struct XVCompose3Out 
	{
		XVImage outImage;
		float outTime;
	};
	//函数名称：创建三通道图像
	MAKEDLL_API int XVCompose3(XVCompose3In& XVCompose3_In, XVCompose3Out& XVCompose3_Out);
	//返回0，算法运行成功
	//返回-1，输出图像内存分配失败
	//返回-2，输入图像必须是灰度图像
	//返回-3，输入图像必须尺寸相同
	//返回-4，输入图像不能为空

	struct XVSetImagePixelsIn 
	{
		XVImage* inImage = NULL;
		XVRegion inRoi;
		XVPixel inPixel;//灰度图像仅需设置inPixel.ch1
	};
	struct XVSetImagePixelsOut 
	{
		XVImage outImage;
		float outTime;
	};
	//函数名称：设置像素值
	MAKEDLL_API int XVSetImagePixels(XVSetImagePixelsIn& XVSetImagePixels_In, XVSetImagePixelsOut& XVSetImagePixels_Out);
	//返回0，算法运行成功
	//返回-1，输出图像内存分配失败
	//返回-2，输入图像为空

	struct XVTileImageOffsetIn 
	{
		vector<XVImage> inImgs;
		XVRectangleOrientation inTileOrientation = XVRectangleOrientation::XVO_Vertical;//拼接方向，可选值{竖直，水平}，默认竖直
	};
	struct XVTileImageOffsetOut 
	{
		XVImage outImage;
		float outTime;
	};
	//函数名称：图像拼接
	MAKEDLL_API int XVTileImageOffset(XVTileImageOffsetIn& XVTileImageOffset_In, XVTileImageOffsetOut& XVTileImageOffset_Out);
	//返回0，算法运行成功
	//返回-1，内存分配失败
	//返回-2，输入图像数组为空
	//返回-3，输入图像类型或尺寸不一致

	struct XVTransposeImageIn 
	{
		XVImage* inImage = NULL;
	};
	struct XVTransposeImageOut 
	{
		XVImage outImage;
		float outTime;
	};
	//函数名称：图像转置
	MAKEDLL_API int XVTransposeImage(XVTransposeImageIn& XVTransposeImage_In, XVTransposeImageOut& XVTransposeImage_Out);
	//返回0，算法运行成功
	//返回-1，图像输入为空
	//返回-2，输出图像内存分配失败

	struct XVRegionTransposeIn 
	{
		XVRegion inRegion;
	};
	struct XVRegionTransposeOut 
	{
		XVRegion outRegion;
		float outTime;
	};
	//函数名称：区域转置
	MAKEDLL_API int XVRegionTranspose(XVRegionTransposeIn& XVRegionTranspose_In, XVRegionTransposeOut& XVRegionTranspose_Out);
	//返回0，算法运行成功
	//返回-1，算法运行失败
	//返回-2，内存分配失败

	struct XVLogarithmImageIn 
	{
		XVImage* inImage = NULL;//输入图像，必须是单通道或3通道图像
		XVRegion inRegion;//输入感兴趣区域
		float inScale;//缩放系数，默认255，范围[-8388607.0，8388608.0]
		float inOffset;//偏移因子，默认1.0，范围[1.0，8388608.0]
		bool inNormalizeZero;//归一化，默认false
	};
	struct XVLogarithmImageOut 
	{
		XVImage outImage;//输出图像
		float outTime;//时间
	};
	//函数名称：图像logarithm变换
	MAKEDLL_API int XVLogarithmImage(XVLogarithmImageIn& XVLogarithmImage_In, XVLogarithmImageOut& XVLogarithmImage_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输出图像内存分配失败

	struct XVNormalizeLocalBrightnessIn 
	{
		XVImage* inImage = NULL;//输入图像，必须是单通道或3通道图像
		XVRegion inRoi;//感兴趣区域
		float inTargetMean = 128.0f;//目标亮度均值，默认128.0，范围[0.0，999999.0]
		float inGammaValue = 1.0f;//伽马系数，默认1.0，范围[0.01，8.0]
		SmoothType inType = SmoothType::XVNGauss;//平滑类型，默认高斯

		//仅在平滑类型为“均值”时，在软件平台参数栏显示
		int inRadiusX = 10;//核宽一半，默认10，范围[0，999999]
		int inRadiusY = 10;//核高一半，默认10，范围[0，999999]

		//仅在平滑类型为“高斯”时，在软件平台参数栏显示
		float inStdDevX = 5.0f;//水平平滑标准差，默认5.0，范围[0.0，999999.0]
		float inStdDevY = 5.0f;//垂直平滑标准差，默认5.0，范围[0.0，999999.0]
	};
	struct XVNormalizeLocalBrightnessOut 
	{
		XVImage outImage;//输出图像
		float outTime;//时间
	};
	//函数名称：图像局部亮度归一化
	MAKEDLL_API int XVNormalizeLocalBrightness(XVNormalizeLocalBrightnessIn& XVNormalizeLocalBrightness_In, XVNormalizeLocalBrightnessOut& XVNormalizeLocalBrightness_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输出图像内存分配失败

	struct XVRobotFlangePoseSortIn 
	{
		vector<RobotFlangePose> inFlanges;
		bool isAscending;//是否升序，默认true(升序)，否则false(降序)
		XVRobotFlangePoseSortFeature inFeature;//排序特征，默认RobotFlangePoseX(X坐标)
	};
	struct XVRobotFlangePoseSortOut 
	{
		vector<RobotFlangePose> outFlanges;//排序后法兰
		vector<size_t> outIndices;//排序后下标
		float outTime;
	};

	//函数名称：法兰坐标排序
	MAKEDLL_API int XVRobotFlangePoseSort(XVRobotFlangePoseSortIn& XVRobotFlangePoseSort_In, XVRobotFlangePoseSortOut& XVRobotFlangePoseSort_Out);
	//返回0，算法运行成功
	//返回-1，输入数组为空

	struct XVCropImageToRectangleIn 
	{
		XVImage* inImage = NULL;//输入图像
		XVRectangle2D inRect;//输入矩形
		XVInterpolationMethod inMethod = XVInterpolationMethod::Bilinear;//插值模式，默认值Bilinear(双线性)
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//输入局部坐标系
	};
	struct XVCropImageToRectangleOut 
	{
		XVImage outImage;//输出图像
		XVRectangle2D outAlignedRect;//跟随后矩形
		XVCoordinateSystem2D outAlignment;//跟随后坐标系
		float outTime;//时间
	};
	//函数名称：图像裁剪(矩形)
	MAKEDLL_API int XVCropImageToRectangle(XVCropImageToRectangleIn& XVCropImageToRectangle_In, XVCropImageToRectangleOut& XVCropImageToRectangle_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输出图像内存分配失败

	struct XVRegionPerimeterIn 
	{
		XVRegion inRegion;//输入区域
	};
	struct XVRegionPerimeterOut 
	{
		float outPerimeter;//周长
		float outTime;//时间
	};
	//函数名称：区域周长
	MAKEDLL_API int XVRegionPerimeter(XVRegionPerimeterIn& XVRegionPerimeter_In, XVRegionPerimeterOut& XVRegionPerimeter_Out);
	//返回0，算法运行成功

	struct XVDrawRegions_SingleColorIn
	{
		XVImage* inImage = NULL;//输入图像
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };
		vector<XVRegion> inRois;//输入绘制区域
		XVPixel inColor = { 0.0f,255.0f,0.0f,255.0f };//输入绘制颜色
		float inOpacity = 1.0f;//不透明度，默认1.0，范围[0.0，1.0]
		bool isRGB = true;//是否输出3通道图像，默认true
	};
	struct XVDrawRegions_SingleColorOut
	{
		XVImage outImage;//输出图像
		float outTime;//时间
	};
	//函数名称：绘制区域(单色)
	MAKEDLL_API int XVDrawRegions_SingleColor(XVDrawRegions_SingleColorIn& XVDrawRegions_SingleColor_In, XVDrawRegions_SingleColorOut& XVDrawRegions_SingleColor_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输出图像内存分配失败
	//返回-3，绘制区域个数不能为空

	struct XVDrawRegions_DoubleColorIn
	{
		XVImage* inImage = NULL;//输入图像
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };
		vector<XVRegion> inRois;//输入绘制区域
		vector<bool>inJudging;//输入颜色判定
		XVPixel inOKColor = { 0.0f,255.0f,0.0f,255.0f };//输入通过颜色
		XVPixel inNGColor = { 0.0f,0.0f,255.0f,255.0f };//输入失败颜色
		float inOpacity = 1.0f;//不透明度，默认1.0，范围[0.0，1.0]
		bool isRGB = true;//是否输出3通道图像，默认true
	};
	struct XVDrawRegions_DoubleColorOut
	{
		XVImage outImage;//输出图像
		float outTime;//时间
	};
	//函数名称：绘制区域(双色)
	MAKEDLL_API int XVDrawRegions_DoubleColor(XVDrawRegions_DoubleColorIn& XVDrawRegions_DoubleColor_In, XVDrawRegions_DoubleColorOut& XVDrawRegions_DoubleColor_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，绘制区域与颜色判定个数必须相等
	//返回-3，输出图像内存分配失败
	//返回-4，绘制区域与颜色判定个数不能为空

	struct XVDrawRegions_MultiColorIn 
	{
		XVImage* inImage = NULL;//输入图像
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };
		vector<XVRegion> inRois;//输入绘制区域
		vector<XVPixel> inColors;//输入绘制颜色
		float inOpacity = 1.0f;//不透明度，默认1.0，范围[0.0，1.0]
		bool isRGB = true;//是否输出3通道图像，默认true
	};
	struct XVDrawRegions_MultiColorOut 
	{
		XVImage outImage;//输出图像
		float outTime;//时间
	};
	//函数名称：绘制区域(多色)
	MAKEDLL_API int XVDrawRegions_MultiColor(XVDrawRegions_MultiColorIn& XVDrawRegions_MultiColor_In, XVDrawRegions_MultiColorOut& XVDrawRegions_MultiColor_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，绘制区域与绘制颜色个数必须相等
	//返回-3，输出图像内存分配失败
	//返回-4，绘制区域与绘制颜色个数不能为空

	struct XVCheckPresenceByIntensityIn
	{
		XVImage* inImage = NULL;//输入图像
		XVRegion inRoi;//检测区域
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//坐标系
		BYTE inMinIntensity = 0;//最小灰度值，范围[0,255],默认0
		BYTE inMaxIntensity = 255;//最大灰度值，范围[0,255],默认255
		float inMinContrast = 0.0f;//最小标准差，范围[0.0f,999999.0f]，默认0.0f
		float inMaxContrast = 1.0f;//最大标准差，范围[0.0f,999999.0f]，默认1.0f
	};
	struct XVCheckPresenceByIntensityOut
	{
		bool outIsPresent;//是否存在标志,true表示存在，false表示不存在
		float outIntensity;//像素平均值
		float outContrast;//像素标准差
		XVRegion outAlignedRoi;//跟随后检测区域
		float outTime;//时间
	};
	//函数名称：检测目标是否存在(灰度)
	MAKEDLL_API int XVCheckPresenceByIntensity(XVCheckPresenceByIntensityIn& XVCheckPresenceByIntensity_In, XVCheckPresenceByIntensityOut& XVCheckPresenceByIntensity_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输入图像必须是单通道或3通道
	//返回-3，动态内存分配失败

	struct XVCheckPresenceByPixelAmountIn
	{
		XVImage* inImage = NULL;//输入图像
		XVRegion inRoi;//检测区域
		XVCoordinateSystem2D inAlignment = { XVOptionalType::NUL,{0.0f,0.0f},0.0f,1.0f };//坐标系
		XVImageType inColorModel = XVImageType::HSV;//颜色模型，下拉菜单中共3个选项HSV（默认）、HSI、HSL
		bool inDiagMode = false;//诊断模式，false(默认)关闭诊断模式，true开启诊断模式
		int inBeginHue = 0;//最小色调值，范围[0,255]，默认0
		int inEndHue = 255;//最大色调值，范围[0,255]，默认255
		int inMinSaturation = 128;//最小饱和度，范围[0,255]，默认128
		int inMaxSaturation = 255;//最大饱和度，范围[0,255]，默认255
		float inMinBrightness = 128.0f;//最小亮度值，范围[0.0,999999.0]，默认128.0
		float inMaxBrightness = 255.0f;//最大亮度值，范围[0.0,999999.0]，默认255.0
		float inMinAmount = 0.5f;//有效像素最小比例，范围[0.0,1.0]，默认0.5
		float inMaxAmount = 1.0f;//有效像素最大比例，范围[0.0,1.0]，默认1.0
	};
	struct XVCheckPresenceByPixelAmountOut
	{
		bool outIsPresent;//是否存在标志,true表示存在，false表示不存在
		float outAmount;//有效像素比例
		XVImage outImage;//诊断图像，当且仅当诊断模式inDiagMode为true时，才有值且需要释放内存
		XVRegion outAlignedRoi;//跟随后检测区域
		XVRegion outForeground;//目标区域
		float outTime;//时间
	};
	//函数名称：检测目标是否存在(像素个数)
	MAKEDLL_API int XVCheckPresenceByPixelAmount(XVCheckPresenceByPixelAmountIn& XVCheckPresenceByPixelAmount_In, XVCheckPresenceByPixelAmountOut& XVCheckPresenceByPixelAmount_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输入图像必须是单通道或3通道
	//返回-3，输入检测区域为空

	struct XVImageHistogramIn
	{
		XVImage* inImage = NULL;//输入图像，必须是单通道或三通道图像
		XVRegion inRoi;//感兴趣区域，默认全图
		int inChannelIndex = 0;//通道索引，默认0，范围[0,2]
	};
	struct XVImageHistogramOut
	{
		vector<int> outHistogram;//直方图
		float outTime;//时间
	};
	//函数名称：图像直方图
	MAKEDLL_API int XVImageHistogram(XVImageHistogramIn& XVImageHistogram_In, XVImageHistogramOut& XVImageHistogram_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，输入图像必须是单通道或3通道

	struct XVHistogramMedianIn
	{
		vector<int> inHistogram;//输入直方图
	};
	struct XVHistogramMedianOut
	{
		float outMedian;//直方图中位数
		float outTime;//时间
	};
	//函数名称：直方图中位数
	MAKEDLL_API int XVHistogramMedian(XVHistogramMedianIn& XVHistogramMedian_In, XVHistogramMedianOut& XVHistogramMedian_Out);
	//返回0，算法运行成功
	//返回-1，输入直方图为空

	struct XVArrayCosineSimilarityIn
	{
		vector<float> inArray1;//数组1
		vector<float> inArray2;//数组2
	};
	struct XVArrayCosineSimilarityOut
	{
		float outSimilarity;//相似度
		float outTime;//时间
	};
	//函数名称：数组余弦相似度
	MAKEDLL_API int XVArrayCosineSimilarity(XVArrayCosineSimilarityIn& XVArrayCosineSimilarity_In, XVArrayCosineSimilarityOut& XVArrayCosineSimilarity_Out);
	//返回0，算法运行成功
	//返回-1，输入数组大小不相等

	struct XVClearnessScoreIn
	{
		XVImage* inImage = NULL;//输入图像，单通道或3通道
		ScoreMethod inMethod = ScoreMethod::BRENNER;//评估方法，默认Sobel梯度下降法
	};
	struct XVClearnessScoreOut
	{
		float outScore;//清晰度得分，分值越高，则图像越清晰
		float outTime;//算法时间,单位ms
	};
	//函数名称：清晰度评估
	MAKEDLL_API int  XVClearnessScore(XVClearnessScoreIn& XVClearnessScore_In, XVClearnessScoreOut& XVClearnessScore_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空

	struct XVImageEncryptionIn
	{
		XVImage* inImg = NULL;//输入图像
		int inSeed = 3000;//种子值，用于生成密钥，范围[1000,4000],默认3000
		EncryptionMethod inMethod = EncryptionMethod::Encryption;//处理方式，默认加密
	};
	struct XVImageEncryptionOut
	{
		XVImage outImg;//输出图像
		float outTime;//时间
	};
	//函数名称：图像加密/解密
	MAKEDLL_API int  XVImageEncryption(XVImageEncryptionIn& XVImageEncryption_In, XVImageEncryptionOut& XVImageEncryption_Out);
	//返回0，算法运行成功
	//返回-1，输入图像为空
	//返回-2，密钥生成失败
	//返回-3，输出图像内存分配失败

	struct XVCreateColorModelUniversalIn
	{
		XVImage* inImage = NULL;//输入图像
		XVRegion inRoi;//学习区域，默认全图
		std::string inColorName;//模板颜色名称
	};
	struct XVCreateColorModelUniversalOut
	{
		int outHistogramHue[360];//模板色相直方图
		int outHistogramSaturation[256];//模板饱和度直方图
		std::string outColorName;//模板颜色名称
		float outTime;//时间
	};
	//函数名称：创建颜色模板
	MAKEDLL_API int XVCreateColorModelUniversal(XVCreateColorModelUniversalIn& XVCreateColorModel_In, XVCreateColorModelUniversalOut& XVCreateColorModel_Out);
	//返回0，算法运行成功
	//返回-1，图像为空
	//返回-2，模板图像必须是彩色图像

	struct XVResultInfo
	{
		std::string name;//颜色名称
		float score;//分数，表示当前图像判定为该颜色的概率
	};

	struct XVColorIdentificationUniversalIn
	{
		XVImage* inImage = NULL;//待识别图像
		XVRegion inRoi;//感兴趣区域，默认空区域(全图)
		vector<XVCreateColorModelUniversalOut> inModels;//颜色模板
	};

	struct XVColorIdentificationUniversalOut
	{
		vector<XVResultInfo> outResults;//识别结果信息，已按得分降序排列
		int outCurrentHistogramHue[360];//色相直方图
		int outCurrentHistogramSaturation[255];//饱和度直方图
		float outTime;//时间
	};
	//函数名称：颜色识别(模板)
	MAKEDLL_API int XVColorIdentificationUniversal(XVColorIdentificationUniversalIn& XVColorIdentification_In, XVColorIdentificationUniversalOut& XVColorIdentification_Out);
	//返回0，算法运行成功
	//返回-1，图像为空
	//返回-2，不是彩色图像
	//返回-3，颜色模型为空

	struct XVScrewThreadIn
	{
		XVPath inPath;//输入路径
		float inMaxDistance = 3.0f;//最大距离，[0.0f,999999.0f]，默认3.0f
	};
	struct XVScrewThreadOut
	{
		vector<XVPoint2D>outPeaks;//峰值点
		vector<XVPoint2D>outValleys;//谷值点
		float outTime;
	};
	//函数名称：定位路径峰谷点(螺纹检测专用)
	MAKEDLL_API int XVScrewThread(XVScrewThreadIn& XVScrewThread_In, XVScrewThreadOut& XVScrewThread_Out);
	//返回0，算法运行成功
	//返回-1，输入路径至少需要包含3个点
	//返回-2，减少路径点数算法运行失败

	struct XVSkeletonizeRegionIn
	{
		XVRegion inRegion;
		int inMethod = 12; //软件中设置下拉菜单，选项只有“八连通”和“十二连通”，默认“十二连通”
	};
	struct XVSkeletonizeRegionOut
	{
		XVRegion outRegion;//骨架区域
		vector<XVPath> outPaths;//区域中轴线
		float outTime;
	};
	//函数名称：提取区域骨架
	MAKEDLL_API int XVSkeletonizeRegion(XVSkeletonizeRegionIn& XVSkeletonizeRegion_In, XVSkeletonizeRegionOut& XVSkeletonizeRegion_Out);
	//返回0，算法运行成功
	//返回-1，内存分配失败
}
#endif